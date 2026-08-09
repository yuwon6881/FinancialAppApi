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
            CategoryAlertsEnabled = status.CategoryAlertsEnabled
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

    // PUT: api/push/category-alerts
    [HttpPut("category-alerts")]
    [AuthorizeToken]
    public async Task<IActionResult> PutCategoryAlerts(PushCategoryAlertsDto dto)
    {
        var updated = await _subscriptionService.SetCategoryAlertsEnabledAsync(
            dto.Enabled,
            HttpContext.RequestAborted);
        if (!updated)
        {
            return Conflict(new { message = "Enable push notifications on at least one device first." });
        }

        return Ok(new PushCategoryAlertsDto { Enabled = dto.Enabled });
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

        await _subscriptionService.SubscribeAsync(dto.DeviceId.Trim(), dto.FcmToken.Trim(), HttpContext.RequestAborted);
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
    public bool Enabled { get; set; }
    public bool DeviceRegistered { get; set; }
    public bool CategoryAlertsEnabled { get; set; }
}

public class PushDeviceDto
{
    public string Id { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }
    public DateTime EnrolledAt { get; set; }
    public DateTime LastUpdatedAt { get; set; }
}

public class PushCategoryAlertsDto
{
    public bool Enabled { get; set; }
}

public class PushSubscribeDto
{
    public string DeviceId { get; set; } = string.Empty;
    public string FcmToken { get; set; } = string.Empty;
}
