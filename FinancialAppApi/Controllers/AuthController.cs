using Microsoft.AspNetCore.Mvc;
using FinancialAppApi.Filters;
using FinancialAppApi.Extensions;
using FinancialAppApi.Services;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AuthAccountService _authAccountService;
    private readonly AuthSessionService _authSessionService;

    public AuthController(AuthAccountService authAccountService, AuthSessionService authSessionService)
    {
        _authAccountService = authAccountService;
        _authSessionService = authSessionService;
    }

    private static string? GetClientIp(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            return forwarded.Split(',')[0].Trim();
        }
        return context.Connection.RemoteIpAddress?.ToString();
    }

    private string? Username => HttpContext.Items["Username"] as string;

    // GET: api/auth/status
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus() => await _authAccountService.GetStatusAsync();

    // POST: api/auth/register
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request) =>
        await _authAccountService.RegisterAsync(request.Username, request.Password);

    // POST: api/auth/login
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request) =>
        await _authAccountService.LoginAsync(
            request.Username,
            request.Password,
            request.DeviceId,
            request.DeviceName,
            GetClientIp(HttpContext),
            HttpContext.Request.Headers["User-Agent"].ToString());

    // POST: api/auth/login/2fa
    [HttpPost("login/2fa")]
    public async Task<IActionResult> LoginTwoFactor([FromBody] TwoFactorLoginRequest request) =>
        await _authAccountService.LoginTwoFactorAsync(
            request.PendingToken,
            request.Code,
            GetClientIp(HttpContext),
            HttpContext.Request.Headers["User-Agent"].ToString());

    // POST: api/auth/logout
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        if (Request.TryGetBearerToken(out var token) == BearerTokenResult.Ok)
        {
            await _authSessionService.LogoutAsync(token);
        }
        return Ok(new { message = "Logged out successfully" });
    }

    // POST: api/auth/lock
    [AuthorizeToken]
    [HttpPost("lock")]
    public async Task<IActionResult> LockSession()
    {
        if (Request.TryGetBearerToken(out var token) == BearerTokenResult.Ok &&
            await _authSessionService.LockSessionAsync(token))
        {
            return Ok(new { message = "Session locked" });
        }
        return BadRequest(new { message = "Invalid session" });
    }

    public class VerifyPasswordRequest
    {
        public string Password { get; set; } = string.Empty;
    }

    // POST: api/auth/verify-password
    [AuthorizeToken]
    [HttpPost("verify-password")]
    public async Task<IActionResult> VerifyPassword([FromBody] VerifyPasswordRequest request)
    {
        Request.TryGetBearerToken(out var currentToken);
        return await _authAccountService.VerifyPasswordAsync(Username, request.Password, currentToken);
    }

    // GET: api/auth/sessions
    [AuthorizeToken]
    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions()
    {
        if (string.IsNullOrEmpty(Username))
        {
            return Unauthorized();
        }

        Request.TryGetBearerToken(out var currentToken);
        var sessions = await _authSessionService.GetSessionsAsync(Username, currentToken);
        return Ok(sessions);
    }

    // POST: api/auth/sessions/heartbeat
    [AuthorizeToken]
    [HttpPost("sessions/heartbeat")]
    public async Task<IActionResult> Heartbeat()
    {
        if (Request.TryGetBearerToken(out var token) == BearerTokenResult.Ok)
        {
            await _authSessionService.HeartbeatAsync(token, GetClientIp(HttpContext), HttpContext.Request.Headers["User-Agent"].ToString());
        }
        return NoContent();
    }

    // DELETE: api/auth/sessions/{id}
    [AuthorizeToken]
    [HttpDelete("sessions/{id:guid}")]
    public async Task<IActionResult> RevokeSession(Guid id)
    {
        if (string.IsNullOrEmpty(Username))
        {
            return Unauthorized();
        }

        await _authSessionService.RevokeSessionAsync(Username, id);
        return NoContent();
    }

    // POST: api/auth/sessions/revoke-all
    [AuthorizeToken]
    [HttpPost("sessions/revoke-all")]
    public async Task<IActionResult> RevokeAllSessions([FromQuery] bool keepCurrent = true)
    {
        if (string.IsNullOrEmpty(Username))
        {
            return Unauthorized();
        }

        Request.TryGetBearerToken(out var currentToken);
        var revokedCount = await _authSessionService.RevokeAllSessionsAsync(Username, currentToken, keepCurrent);
        return Ok(new { revokedCount });
    }

    // POST: api/auth/change-password
    [AuthorizeToken]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        Request.TryGetBearerToken(out var currentToken);
        return await _authAccountService.ChangePasswordAsync(Username, request.CurrentPassword, request.NewPassword, currentToken);
    }

    // GET: api/auth/2fa/status
    [AuthorizeToken]
    [HttpGet("2fa/status")]
    public async Task<IActionResult> GetTwoFactorStatus() => await _authAccountService.GetTwoFactorStatusAsync(Username);

    // POST: api/auth/2fa/totp/setup
    [AuthorizeToken]
    [HttpPost("2fa/totp/setup")]
    public async Task<IActionResult> SetupTotp() => await _authAccountService.SetupTotpAsync(Username);

    // POST: api/auth/2fa/totp/enable
    [AuthorizeToken]
    [HttpPost("2fa/totp/enable")]
    public async Task<IActionResult> EnableTotp([FromBody] VerifyCodeRequest request)
    {
        Request.TryGetBearerToken(out var currentToken);
        return await _authAccountService.EnableTotpAsync(Username, request.Code, currentToken);
    }

    // POST: api/auth/2fa/totp/disable
    [AuthorizeToken]
    [HttpPost("2fa/totp/disable")]
    public async Task<IActionResult> DisableTotp([FromBody] DisableTotpRequest request) =>
        await _authAccountService.DisableTotpAsync(Username, request.Password, request.Code);

    // POST: api/auth/2fa/recovery-codes/regenerate
    [AuthorizeToken]
    [HttpPost("2fa/recovery-codes/regenerate")]
    public async Task<IActionResult> RegenerateRecoveryCodes([FromBody] RegenerateRecoveryCodesRequest request) =>
        await _authAccountService.RegenerateRecoveryCodesAsync(Username, request.Password);
}

public class RegisterRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
}

public class TwoFactorLoginRequest
{
    public string PendingToken { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class VerifyCodeRequest
{
    public string Code { get; set; } = string.Empty;
}

public class DisableTotpRequest
{
    public string Password { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

public class RegenerateRecoveryCodesRequest
{
    public string Password { get; set; } = string.Empty;
}
