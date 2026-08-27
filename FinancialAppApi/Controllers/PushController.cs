using Microsoft.AspNetCore.Mvc;
using FinancialAppApi.Filters;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Push;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/push")]
public class PushController : ControllerBase
{
    private readonly PushSubscriptionService _subscriptionService;
    private readonly PushDispatchService _dispatchService;
    private readonly CategoryLimitAlertProcessor _categoryLimitAlertProcessor;

    public PushController(
        PushSubscriptionService subscriptionService,
        PushDispatchService dispatchService,
        CategoryLimitAlertProcessor categoryLimitAlertProcessor)
    {
        _subscriptionService = subscriptionService;
        _dispatchService = dispatchService;
        _categoryLimitAlertProcessor = categoryLimitAlertProcessor;
    }

    // GET: api/push/status?deviceId=...
    [HttpGet("status")]
    [AuthorizeToken]
    public async Task<ActionResult<PushStatusDto>> GetStatus([FromQuery] string? deviceId)
    {
        var status = await _subscriptionService.GetStatusAsync(deviceId, HttpContext.RequestAborted);
        return Ok(new PushStatusDto
        {
            Enabled = status.AccountEnabled,
            DeviceRegistered = status.DeviceSubscribed,
            TokenRenewalRequired = status.TokenRenewalRequired,
            BillRemindersEnabled = status.ThisDeviceBillReminders,
            CategoryAlertsEnabled = status.ThisDeviceCategoryAlerts,
            OtherDevicesBillReminders = status.OtherDevicesBillReminders,
            OtherDevicesCategoryAlerts = status.OtherDevicesCategoryAlerts
        });
    }

    // GET: api/push/devices?deviceId=...
    [HttpGet("devices")]
    [AuthorizeToken]
    public async Task<ActionResult<IReadOnlyList<PushDeviceDto>>> GetDevices([FromQuery] string? deviceId)
    {
        var devices = await _subscriptionService.GetDevicesAsync(deviceId, HttpContext.RequestAborted);
        return Ok(devices.Select(device => new PushDeviceDto
        {
            Id = device.Id,
            IsCurrent = device.IsCurrent,
            BillRemindersEnabled = device.BillReminders,
            CategoryAlertsEnabled = device.CategoryAlerts,
            EnrolledAt = device.CreatedAt,
            LastUpdatedAt = device.UpdatedAt
        }).ToList());
    }

    // DELETE: api/push/devices/{id}
    [HttpDelete("devices/{id}")]
    [AuthorizeToken]
    public async Task<IActionResult> RevokeDevice(string id)
    {
        var revoked = await _subscriptionService.RevokeDeviceAsync(id, HttpContext.RequestAborted);
        return revoked ? NoContent() : NotFound();
    }

    // DELETE: api/push/subscriptions/{deviceId}/channels/{channel}
    // Turning one kind off needs no FCM token, so it is its own route rather than an upsert.
    // There is deliberately no route here that turns a channel *on*: enabling has to prove
    // browser permission and produce a fresh token, which is what PutSubscription is for.
    [HttpDelete("subscriptions/{deviceId}/channels/{channel}")]
    [AuthorizeToken]
    public async Task<IActionResult> DeleteChannel(string deviceId, string channel)
    {
        if (!PushChannel.IsKnown(channel))
        {
            return BadRequest(new { message = "Unknown notification kind." });
        }

        await _subscriptionService.DisableChannelAsync(deviceId, channel, HttpContext.RequestAborted);
        // Idempotent by design: a device that was already off for this kind is the state the
        // caller asked for, and reporting 404 would make an offline retry look like a failure.
        return NoContent();
    }

    // PUT: api/push/subscriptions
    [HttpPut("subscriptions")]
    [AuthorizeToken]
    public async Task<IActionResult> PutSubscription(PushSubscribeDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.DeviceId) || string.IsNullOrWhiteSpace(dto.FcmToken))
        {
            return BadRequest(new { message = "deviceId and fcmToken are required." });
        }
        if (dto.DeviceId.Trim().Length > 200 || dto.FcmToken.Trim().Length > 4096)
        {
            return BadRequest(new { message = "deviceId or fcmToken is too long." });
        }

        // Omitted flags mean "leave this device's other choice alone", so enabling one kind can
        // never switch on the other as a side effect.
        var subscription = await _subscriptionService.SubscribeAsync(
            dto.DeviceId.Trim(),
            dto.FcmToken.Trim(),
            dto.BillReminders,
            dto.CategoryAlerts,
            HttpContext.RequestAborted);

        // Keyed on what this device now RECEIVES, not on whether this particular request asked to
        // turn the channel on. The client re-registers its rotated token on every launch and omits
        // the channel flags when the device is already enrolled, so testing dto.CategoryAlerts
        // meant the launch that could have drained a waiting alert never did -- only the rarer
        // explicit opt-in did. Undelivered spending alerts are exactly the ones that need a second
        // chance: their inline attempt runs after the response and can be starved, and the
        // scheduled dispatch does not run until 09:00 local.
        if (subscription?.CategoryAlertsEnabled == true)
        {
            await _categoryLimitAlertProcessor.ProcessPendingAsync(HttpContext.RequestAborted);
        }
        return Ok();
    }

    // DELETE: api/push/subscriptions/{deviceId}
    [HttpDelete("subscriptions/{deviceId}")]
    [AuthorizeToken]
    public async Task<IActionResult> DeleteSubscription(string deviceId)
    {
        await _subscriptionService.UnsubscribeAsync(deviceId, HttpContext.RequestAborted);
        return NoContent();
    }

    // POST: api/push/dispatch — invoked only by Cloud Scheduler with a Google-signed OIDC token.
    //
    // The status code is the only part of this response the caller can act on, and Cloud
    // Scheduler's retry policy is the only thing that gets a transiently lost reminder a second
    // chance inside the day its TTL allows. So a run that could not deliver answers 503 rather
    // than 200: a wholly dead pipeline (missing Fcm:ProjectId, no send credential, FCM
    // unreachable) used to look identical in Scheduler history to a day with nothing due.
    // Anything already sent still reports 200 — retrying a partially successful run would only
    // re-walk claims that are already committed.
    [HttpPost("dispatch")]
    [AuthorizeGoogleOidc]
    public async Task<IActionResult> Dispatch()
    {
        var summary = await _dispatchService.DispatchAsync(HttpContext.RequestAborted);
        var body = new
        {
            sent = summary.Sent,
            skipped = summary.Skipped,
            disabled = summary.Disabled,
            failed = summary.Failed,
            configured = summary.Configured
        };

        if (!summary.Configured || (summary.Failed > 0 && summary.Sent == 0))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, body);
        }
        return Ok(body);
    }
}

public class PushStatusDto
{
    /// <summary>Any device on the account receives something. Never this device's state.</summary>
    public bool Enabled { get; set; }
    public bool DeviceRegistered { get; set; }
    /// <summary>The server retired this device's old token; do not upload Firebase's cached value.</summary>
    public bool TokenRenewalRequired { get; set; }
    /// <summary>What THIS device receives. Both switches render from these two.</summary>
    public bool BillRemindersEnabled { get; set; }
    public bool CategoryAlertsEnabled { get; set; }
    /// <summary>Whether some other device receives it — informational only, never a switch state.</summary>
    public bool OtherDevicesBillReminders { get; set; }
    public bool OtherDevicesCategoryAlerts { get; set; }
}

public class PushDeviceDto
{
    public string Id { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }
    public bool BillRemindersEnabled { get; set; }
    public bool CategoryAlertsEnabled { get; set; }
    public DateTime EnrolledAt { get; set; }
    public DateTime LastUpdatedAt { get; set; }
}

public class PushSubscribeDto
{
    public string DeviceId { get; set; } = string.Empty;
    public string FcmToken { get; set; } = string.Empty;
    public bool? BillReminders { get; set; }
    public bool? CategoryAlerts { get; set; }
}
