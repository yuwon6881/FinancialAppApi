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

    public PushController(PushSubscriptionService subscriptionService, PushDispatchService dispatchService)
    {
        _subscriptionService = subscriptionService;
        _dispatchService = dispatchService;
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
        await _subscriptionService.SubscribeAsync(
            dto.DeviceId.Trim(),
            dto.FcmToken.Trim(),
            dto.BillReminders,
            dto.CategoryAlerts,
            HttpContext.RequestAborted);
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
    [HttpPost("dispatch")]
    [AuthorizeGoogleOidc]
    public async Task<IActionResult> Dispatch()
    {
        var summary = await _dispatchService.DispatchAsync(HttpContext.RequestAborted);
        return Ok(new
        {
            sent = summary.Sent,
            skipped = summary.Skipped,
            disabled = summary.Disabled
        });
    }
}

public class PushStatusDto
{
    /// <summary>Any device on the account receives something. Never this device's state.</summary>
    public bool Enabled { get; set; }
    public bool DeviceRegistered { get; set; }
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
