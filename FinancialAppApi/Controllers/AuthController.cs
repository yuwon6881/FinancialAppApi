using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.AspNetCore.Identity;
using FinancialAppApi.Filters;
using FinancialAppApi.Extensions;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly PasswordHasher<string> _passwordHasher;

    public AuthController(AppDbContext context)
    {
        _context = context;
        _passwordHasher = new PasswordHasher<string>();
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

        var result = _passwordHasher.VerifyHashedPassword(user.Username, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            return Unauthorized(new { message = "Invalid username or password" });
        }

        // 1. Clean up expired sessions
        var expiredSessions = await _context.UserSessions
            .Where(s => s.ExpiresAt < DateTime.UtcNow)
            .ToListAsync();
        if (expiredSessions.Count > 0)
        {
            _context.UserSessions.RemoveRange(expiredSessions);
        }

        // 2. Clean up old sessions for THIS device
        if (!string.IsNullOrEmpty(request.DeviceId))
        {
            var deviceSessions = await _context.UserSessions
                .Where(s => s.Username == user.Username && s.DeviceId == request.DeviceId)
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

        // 3. Enforce maximum of 5 active sessions per user
        var activeSessions = await _context.UserSessions
            .Where(s => s.Username == user.Username)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
            
        // We want at most 5 total. Since we are adding 1, we can only keep the 4 newest existing ones.
        if (activeSessions.Count >= 5)
        {
            var sessionsToDrop = activeSessions.Skip(4).ToList();
            _context.UserSessions.RemoveRange(sessionsToDrop);
        }

        // Create new session token
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var session = new UserSession
        {
            Token = token,
            Username = user.Username,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7), // Token valid for 7 days
            DeviceId = request.DeviceId,
            DeviceName = request.DeviceName
        };

        _context.UserSessions.Add(session);
        await _context.SaveChangesAsync();

        return Ok(new { token = token, username = user.Username });
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

        var sessions = await _context.UserSessions
            .Where(s => s.Username == username)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new
            {
                s.Token,
                s.DeviceName,
                s.CreatedAt,
                s.ExpiresAt,
                s.IsLocked
            })
            .ToListAsync();

        return Ok(sessions);
    }

    // DELETE: api/auth/sessions/{token}
    [AuthorizeToken]
    [HttpDelete("sessions/{tokenToRevoke}")]
    public async Task<IActionResult> RevokeSession(string tokenToRevoke)
    {
        var username = HttpContext.Items["Username"] as string;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized();
        }

        var session = await _context.UserSessions
            .FirstOrDefaultAsync(s => s.Token == tokenToRevoke && s.Username == username);

        if (session != null)
        {
            _context.UserSessions.Remove(session);
            await _context.SaveChangesAsync();
        }

        return NoContent();
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
