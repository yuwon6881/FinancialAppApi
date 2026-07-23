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
            DeviceRegistered = status.DeviceSubscribed
        });
    }

    // PUT: api/push/settings
    [HttpPut("settings")]
    [AuthorizeToken]
    public async Task<IActionResult> PutSettings(PushSettingsDto dto)
    {
        var updated = await _subscriptionService.SetAccountEnabledAsync(dto.Enabled, HttpContext.RequestAborted);
        if (!updated)
        {
            return NotFound();
        }

        return Ok(new PushSettingsDto { Enabled = dto.Enabled });
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
}

public class PushSettingsDto
{
    public bool Enabled { get; set; }
}

public class PushSubscribeDto
{
    public string DeviceId { get; set; } = string.Empty;
    public string FcmToken { get; set; } = string.Empty;
}
