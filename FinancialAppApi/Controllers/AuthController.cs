using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.AspNetCore.Identity;
using FinancialAppApi.Filters;
using FinancialAppApi.Extensions;
using FinancialAppApi.Services;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly PasswordHasher<string> _passwordHasher;
    private readonly IConfiguration _configuration;
    private readonly TotpService _totpService;
    private readonly SecretProtector _secretProtector;
    private readonly RecoveryCodeService _recoveryCodeService;

    private const int PendingTwoFactorTtlMinutes = 5;
    private const int MaxCodeAttempts = 5;

    public AuthController(
        AppDbContext context,
        IConfiguration configuration,
        TotpService totpService,
        SecretProtector secretProtector,
        RecoveryCodeService recoveryCodeService)
    {
        _context = context;
        _passwordHasher = new PasswordHasher<string>();
        _configuration = configuration;
        _totpService = totpService;
        _secretProtector = secretProtector;
        _recoveryCodeService = recoveryCodeService;
    }

    private int MaxFailedLoginAttempts => _configuration.GetValue("Auth:MaxFailedLoginAttempts", 5);
    private int LockoutMinutes => _configuration.GetValue("Auth:LockoutMinutes", 15);

    private static string? GetClientIp(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            return forwarded.Split(',')[0].Trim();
        }
        return context.Connection.RemoteIpAddress?.ToString();
    }

    // Shared by direct password login and post-2FA login: cleans up expired/duplicate sessions,
    // enforces the per-user session cap, and issues a new session token for (deviceId, deviceName).
    private async Task<UserSession> CreateSessionAsync(AppUser user, string? deviceId, string? deviceName)
    {
        // 1. Clean up expired sessions
        var expiredSessions = await _context.UserSessions
            .Where(s => s.ExpiresAt < DateTime.UtcNow)
            .ToListAsync();
        if (expiredSessions.Count > 0)
        {
            _context.UserSessions.RemoveRange(expiredSessions);
        }

        // 2. Clean up old sessions for THIS device
        if (!string.IsNullOrEmpty(deviceId))
        {
            var deviceSessions = await _context.UserSessions
                .Where(s => s.Username == user.Username && s.DeviceId == deviceId)
                .ToListAsync();
            if (deviceSessions.Count > 0)
            {
                _context.UserSessions.RemoveRange(deviceSessions);
            }
        }
        else
        {
            // Fallback: If no DeviceId is provided, fallback to old behavior (wipe all password sessions for this user)
            var oldPasswordSessions = await _context.UserSessions
                .Where(s => s.Username == user.Username && s.CredentialId == null)
                .ToListAsync();
            if (oldPasswordSessions.Count > 0)
            {
                _context.UserSessions.RemoveRange(oldPasswordSessions);
            }
        }

        // 3. Enforce maximum of 5 active sessions per user, evicting the least-recently-active
        // session first (falling back to CreatedAt for sessions that have never sent a heartbeat)
        // rather than the oldest by creation time -- an actively-used older session shouldn't lose
        // to a newer one that's actually gone idle.
        var activeSessions = await _context.UserSessions
            .Where(s => s.Username == user.Username)
            .ToListAsync();
        var orderedByActivity = activeSessions
            .OrderByDescending(s => s.LastActiveAt ?? s.CreatedAt)
            .ToList();

        // We want at most 5 total. Since we are adding 1, we can only keep the 4 most active existing ones.
        if (orderedByActivity.Count >= 5)
        {
            var sessionsToDrop = orderedByActivity.Skip(4).ToList();
            _context.UserSessions.RemoveRange(sessionsToDrop);
        }

        // Create new session token
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var session = new UserSession
        {
            Token = token,
            Username = user.Username,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7), // Token valid for 7 days
            DeviceId = deviceId,
            DeviceName = deviceName,
            LastActiveAt = now,
            IpAddress = GetClientIp(HttpContext),
            UserAgent = HttpContext.Request.Headers["User-Agent"].ToString()
        };

        _context.UserSessions.Add(session);
        await _context.SaveChangesAsync();

        return session;
    }

    // GET: api/auth/status
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var hasUser = await _context.AppUsers.AnyAsync();
        var hasFingerprint = hasUser && await _context.WebAuthnCredentials.AnyAsync();
        return Ok(new { isRegistered = hasUser, hasFingerprint });
    }

    // POST: api/auth/register
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (await _context.AppUsers.AnyAsync())
        {
            return BadRequest(new { message = "Registration is closed. A user is already registered." });
        }

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { message = "Username and password are required." });
        }

        var user = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Username = request.Username.Trim()
        };
        user.PasswordHash = _passwordHasher.HashPassword(user.Username, request.Password);

        _context.AppUsers.Add(user);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Registration successful" });
    }

    // POST: api/auth/login
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { message = "Username and password are required." });
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username.ToLower() == request.Username.ToLower());
        if (user == null)
        {
            return Unauthorized(new { message = "Invalid username or password" });
        }

        if (user.LockedUntil.HasValue && user.LockedUntil.Value > DateTime.UtcNow)
        {
            var minutesLeft = Math.Ceiling((user.LockedUntil.Value - DateTime.UtcNow).TotalMinutes);
            return StatusCode(429, new { message = $"Too many failed attempts. Try again in {minutesLeft} minute(s)." });
        }

        var result = _passwordHasher.VerifyHashedPassword(user.Username, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            user.FailedLoginAttempts += 1;
            if (user.FailedLoginAttempts >= MaxFailedLoginAttempts)
            {
                user.LockedUntil = DateTime.UtcNow.AddMinutes(LockoutMinutes);
                user.FailedLoginAttempts = 0;
            }
            await _context.SaveChangesAsync();
            return Unauthorized(new { message = "Invalid username or password" });
        }

        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;

        if (user.TotpEnabled)
        {
            var pending = new PendingTwoFactor
            {
                Username = user.Username,
                DeviceId = request.DeviceId,
                DeviceName = request.DeviceName,
                ExpiresAt = DateTime.UtcNow.AddMinutes(PendingTwoFactorTtlMinutes)
            };
            _context.PendingTwoFactors.Add(pending);
            await _context.SaveChangesAsync();
            return Ok(new { requiresTwoFactor = true, pendingToken = pending.Id.ToString() });
        }

        await _context.SaveChangesAsync();
        var session = await CreateSessionAsync(user, request.DeviceId, request.DeviceName);
        return Ok(new { token = session.Token, username = user.Username });
    }

    // POST: api/auth/login/2fa
    [HttpPost("login/2fa")]
    public async Task<IActionResult> LoginTwoFactor([FromBody] TwoFactorLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PendingToken) || !Guid.TryParse(request.PendingToken, out var pendingId))
        {
            return Unauthorized(new { message = "Login session expired. Please log in again." });
        }

        var pending = await _context.PendingTwoFactors.FirstOrDefaultAsync(p => p.Id == pendingId);
        if (pending == null || pending.ExpiresAt < DateTime.UtcNow)
        {
            if (pending != null)
            {
                _context.PendingTwoFactors.Remove(pending);
                await _context.SaveChangesAsync();
            }
            return Unauthorized(new { message = "Login session expired. Please log in again." });
        }

        if (pending.Attempts >= MaxCodeAttempts)
        {
            _context.PendingTwoFactors.Remove(pending);
            await _context.SaveChangesAsync();
            return Unauthorized(new { message = "Too many attempts. Please log in again." });
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username == pending.Username);
        if (user == null || !user.TotpEnabled || string.IsNullOrEmpty(user.TotpSecret))
        {
            _context.PendingTwoFactors.Remove(pending);
            await _context.SaveChangesAsync();
            return Unauthorized(new { message = "Login session expired. Please log in again." });
        }

        var secret = _secretProtector.Unprotect(user.TotpSecret);
        var validTotp = _totpService.ValidateCode(secret, request.Code);
        var validRecovery = !validTotp && await _recoveryCodeService.TryConsumeAsync(user.Username, request.Code);

        if (!validTotp && !validRecovery)
        {
            pending.Attempts += 1;
            await _context.SaveChangesAsync();
            return Unauthorized(new { message = "Invalid code" });
        }

        _context.PendingTwoFactors.Remove(pending);
        await _context.SaveChangesAsync();

        var session = await CreateSessionAsync(user, pending.DeviceId, pending.DeviceName);
        return Ok(new { token = session.Token, username = user.Username });
    }

    // POST: api/auth/logout
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        if (Request.TryGetBearerToken(out var token) == BearerTokenResult.Ok)
        {
            var session = await _context.UserSessions.FirstOrDefaultAsync(s => s.Token == token);
            if (session != null)
            {
                _context.UserSessions.Remove(session);
                await _context.SaveChangesAsync();
            }
        }
        return Ok(new { message = "Logged out successfully" });
    }

    // POST: api/auth/lock
    [AuthorizeToken]
    [HttpPost("lock")]
    public async Task<IActionResult> LockSession()
    {
        if (Request.TryGetBearerToken(out var token) == BearerTokenResult.Ok)
        {
            var session = await _context.UserSessions.FirstOrDefaultAsync(s => s.Token == token);
            if (session != null)
            {
                session.IsLocked = true;
                await _context.SaveChangesAsync();
                return Ok(new { message = "Session locked" });
            }
        }
        return BadRequest(new { message = "Invalid session" });
    }

    public class VerifyPasswordRequest
    {
        public string Password { get; set; } = string.Empty;
    }

    // POST: api/auth/verify-password
    // Note: this only ever checks the password, never a 2FA code. It's used both to reveal
    // sensitive figures and to unlock an inactivity-locked session, and 2FA is intentionally not
    // required to unlock a session that was already fully authenticated.
    [AuthorizeToken]
    [HttpPost("verify-password")]
    public async Task<IActionResult> VerifyPassword([FromBody] VerifyPasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { message = "Password is required" });
        }

        var username = HttpContext.Items["Username"] as string;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized(new { message = "User not found in session" });
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());
        if (user == null)
        {
            return Unauthorized(new { message = "User not found" });
        }

        var result = _passwordHasher.VerifyHashedPassword(user.Username, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            return Ok(new { verified = false, message = "Incorrect password" });
        }

        // Unlock the session if verified successfully
        if (Request.TryGetBearerToken(out var token) == BearerTokenResult.Ok)
        {
            var session = await _context.UserSessions.FirstOrDefaultAsync(s => s.Token == token);
            if (session != null)
            {
                session.IsLocked = false;
                await _context.SaveChangesAsync();
            }
        }

        return Ok(new { verified = true });
    }

    // GET: api/auth/sessions
    [AuthorizeToken]
    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions()
    {
        var username = HttpContext.Items["Username"] as string;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized();
        }

        var expired = await _context.UserSessions
            .Where(s => s.Username == username && s.ExpiresAt < DateTime.UtcNow)
            .ToListAsync();
        if (expired.Count > 0)
        {
            _context.UserSessions.RemoveRange(expired);
            await _context.SaveChangesAsync();
        }

        Request.TryGetBearerToken(out var currentToken);

        var sessions = await _context.UserSessions
            .Where(s => s.Username == username)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new
            {
                s.Id,
                s.DeviceName,
                s.CreatedAt,
                s.ExpiresAt,
                s.IsLocked,
                s.LastActiveAt,
                s.IpAddress,
                s.UserAgent,
                IsCurrent = s.Token == currentToken
            })
            .ToListAsync();

        return Ok(sessions);
    }

    // POST: api/auth/sessions/heartbeat
    [AuthorizeToken]
    [HttpPost("sessions/heartbeat")]
    public async Task<IActionResult> Heartbeat()
    {
        if (Request.TryGetBearerToken(out var token) == BearerTokenResult.Ok)
        {
            var session = await _context.UserSessions.FirstOrDefaultAsync(s => s.Token == token);
            if (session != null)
            {
                session.LastActiveAt = DateTime.UtcNow;
                session.IpAddress = GetClientIp(HttpContext);
                session.UserAgent = HttpContext.Request.Headers["User-Agent"].ToString();
                await _context.SaveChangesAsync();
            }
        }
        return NoContent();
    }

    // DELETE: api/auth/sessions/{id}
    [AuthorizeToken]
    [HttpDelete("sessions/{id:guid}")]
    public async Task<IActionResult> RevokeSession(Guid id)
    {
        var username = HttpContext.Items["Username"] as string;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized();
        }

        var session = await _context.UserSessions
            .FirstOrDefaultAsync(s => s.Id == id && s.Username == username);

        if (session != null)
        {
            _context.UserSessions.Remove(session);
            await _context.SaveChangesAsync();
        }

        return NoContent();
    }

    // POST: api/auth/sessions/revoke-all
    [AuthorizeToken]
    [HttpPost("sessions/revoke-all")]
    public async Task<IActionResult> RevokeAllSessions([FromQuery] bool keepCurrent = true)
    {
        var username = HttpContext.Items["Username"] as string;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized();
        }

        Request.TryGetBearerToken(out var currentToken);

        var sessions = await _context.UserSessions
            .Where(s => s.Username == username && (!keepCurrent || s.Token != currentToken))
            .ToListAsync();

        _context.UserSessions.RemoveRange(sessions);
        await _context.SaveChangesAsync();

        return Ok(new { revokedCount = sessions.Count });
    }

    // POST: api/auth/change-password
    [AuthorizeToken]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new { message = "Current and new password are required." });
        }

        var username = HttpContext.Items["Username"] as string;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized();
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null)
        {
            return Unauthorized();
        }

        var result = _passwordHasher.VerifyHashedPassword(user.Username, user.PasswordHash, request.CurrentPassword);
        if (result == PasswordVerificationResult.Failed)
        {
            return BadRequest(new { message = "Current password is incorrect." });
        }

        user.PasswordHash = _passwordHasher.HashPassword(user.Username, request.NewPassword);

        Request.TryGetBearerToken(out var currentToken);
        var otherSessions = await _context.UserSessions
            .Where(s => s.Username == username && s.Token != currentToken)
            .ToListAsync();
        _context.UserSessions.RemoveRange(otherSessions);

        await _context.SaveChangesAsync();

        return Ok(new { message = "Password changed successfully.", revokedOtherSessions = otherSessions.Count });
    }

    // GET: api/auth/2fa/status
    [AuthorizeToken]
    [HttpGet("2fa/status")]
    public async Task<IActionResult> GetTwoFactorStatus()
    {
        var username = HttpContext.Items["Username"] as string;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized();
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null)
        {
            return Unauthorized();
        }

        return Ok(new { enabled = user.TotpEnabled });
    }

    // POST: api/auth/2fa/totp/setup
    [AuthorizeToken]
    [HttpPost("2fa/totp/setup")]
    public async Task<IActionResult> SetupTotp()
    {
        var username = HttpContext.Items["Username"] as string;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized();
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null)
        {
            return Unauthorized();
        }

        if (user.TotpEnabled)
        {
            return BadRequest(new { message = "Two-factor authentication is already enabled." });
        }

        var secret = _totpService.GenerateSecret();
        user.PendingTotpSecret = _secretProtector.Protect(secret);
        await _context.SaveChangesAsync();

        return Ok(new { secret, otpauthUri = _totpService.BuildOtpAuthUri(secret, username) });
    }

    // POST: api/auth/2fa/totp/enable
    [AuthorizeToken]
    [HttpPost("2fa/totp/enable")]
    public async Task<IActionResult> EnableTotp([FromBody] VerifyCodeRequest request)
    {
        var username = HttpContext.Items["Username"] as string;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized();
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null || string.IsNullOrEmpty(user.PendingTotpSecret))
        {
            return BadRequest(new { message = "Start two-factor setup first." });
        }

        var secret = _secretProtector.Unprotect(user.PendingTotpSecret);
        if (!_totpService.ValidateCode(secret, request.Code ?? string.Empty))
        {
            return BadRequest(new { message = "Invalid code." });
        }

        user.TotpSecret = user.PendingTotpSecret;
        user.PendingTotpSecret = null;
        user.TotpEnabled = true;

        var recoveryCodes = await _recoveryCodeService.RegenerateAsync(username);

        Request.TryGetBearerToken(out var currentToken);
        var otherSessions = await _context.UserSessions
            .Where(s => s.Username == username && s.Token != currentToken)
            .ToListAsync();
        _context.UserSessions.RemoveRange(otherSessions);

        await _context.SaveChangesAsync();

        return Ok(new { enabled = true, recoveryCodes });
    }

    // POST: api/auth/2fa/totp/disable
    [AuthorizeToken]
    [HttpPost("2fa/totp/disable")]
    public async Task<IActionResult> DisableTotp([FromBody] DisableTotpRequest request)
    {
        var username = HttpContext.Items["Username"] as string;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized();
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null || !user.TotpEnabled || string.IsNullOrEmpty(user.TotpSecret))
        {
            return BadRequest(new { message = "Two-factor authentication is not enabled." });
        }

        var passwordResult = _passwordHasher.VerifyHashedPassword(user.Username, user.PasswordHash, request.Password ?? string.Empty);
        if (passwordResult == PasswordVerificationResult.Failed)
        {
            return BadRequest(new { message = "Incorrect password." });
        }

        var secret = _secretProtector.Unprotect(user.TotpSecret);
        var validTotp = _totpService.ValidateCode(secret, request.Code ?? string.Empty);
        var validRecovery = !validTotp && await _recoveryCodeService.TryConsumeAsync(username, request.Code ?? string.Empty);
        if (!validTotp && !validRecovery)
        {
            return BadRequest(new { message = "Invalid code." });
        }

        user.TotpEnabled = false;
        user.TotpSecret = null;
        user.PendingTotpSecret = null;
        await _context.SaveChangesAsync();
        await _recoveryCodeService.DeleteAllAsync(username);

        return Ok(new { message = "Two-factor authentication disabled." });
    }

    // POST: api/auth/2fa/recovery-codes/regenerate
    [AuthorizeToken]
    [HttpPost("2fa/recovery-codes/regenerate")]
    public async Task<IActionResult> RegenerateRecoveryCodes([FromBody] RegenerateRecoveryCodesRequest request)
    {
        var username = HttpContext.Items["Username"] as string;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized();
        }

        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null || !user.TotpEnabled)
        {
            return BadRequest(new { message = "Two-factor authentication is not enabled." });
        }

        var passwordResult = _passwordHasher.VerifyHashedPassword(user.Username, user.PasswordHash, request.Password ?? string.Empty);
        if (passwordResult == PasswordVerificationResult.Failed)
        {
            return BadRequest(new { message = "Incorrect password." });
        }

        var codes = await _recoveryCodeService.RegenerateAsync(username);
        return Ok(new { recoveryCodes = codes });
    }
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
