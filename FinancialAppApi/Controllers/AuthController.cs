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
        return Ok(new { isRegistered = hasUser });
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

        // Clean up expired sessions first
        var expiredSessions = await _context.UserSessions.Where(s => s.ExpiresAt < DateTime.UtcNow).ToListAsync();
        if (expiredSessions.Any())
        {
            _context.UserSessions.RemoveRange(expiredSessions);
        }

        // Create new session token
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var session = new UserSession
        {
            Token = token,
            Username = user.Username,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7) // Token valid for 7 days
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
}
