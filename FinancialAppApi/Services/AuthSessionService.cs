using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public sealed record SessionSummary(
    Guid Id,
    string? DeviceName,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    bool IsLocked,
    DateTime? LastActiveAt,
    string? IpAddress,
    string? UserAgent,
    bool IsCurrent);

public class AuthSessionService
{
    private readonly AppDbContext _context;

    public AuthSessionService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<UserSession> CreateSessionAsync(
        AppUser user,
        string? deviceId,
        string? deviceName,
        string? ipAddress,
        string? userAgent,
        byte[]? credentialId = null)
    {
        var expiredSessions = await _context.UserSessions
            .Where(s => s.ExpiresAt < DateTime.UtcNow)
            .ToListAsync();
        if (expiredSessions.Count > 0)
        {
            _context.UserSessions.RemoveRange(expiredSessions);
        }

        if (credentialId != null)
        {
            var priorSessionsForCredential = await _context.UserSessions
                .Where(s => s.CredentialId != null && s.CredentialId == credentialId)
                .ToListAsync();
            if (priorSessionsForCredential.Count > 0)
            {
                _context.UserSessions.RemoveRange(priorSessionsForCredential);
            }
        }

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
        else if (credentialId == null)
        {
            var oldPasswordSessions = await _context.UserSessions
                .Where(s => s.Username == user.Username && s.CredentialId == null)
                .ToListAsync();
            if (oldPasswordSessions.Count > 0)
            {
                _context.UserSessions.RemoveRange(oldPasswordSessions);
            }
        }

        var activeSessions = await _context.UserSessions
            .Where(s => s.Username == user.Username)
            .ToListAsync();
        var orderedByActivity = activeSessions
            .OrderByDescending(s => s.LastActiveAt ?? s.CreatedAt)
            .ToList();

        if (orderedByActivity.Count >= 5)
        {
            _context.UserSessions.RemoveRange(orderedByActivity.Skip(4).ToList());
        }

        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var session = new UserSession
        {
            Token = token,
            Username = user.Username,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7),
            CredentialId = credentialId,
            DeviceId = deviceId,
            DeviceName = deviceName,
            LastActiveAt = now,
            IpAddress = ipAddress,
            UserAgent = userAgent
        };

        _context.UserSessions.Add(session);
        await _context.SaveChangesAsync();

        return session;
    }

    public async Task LogoutAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;

        var session = await _context.UserSessions.FirstOrDefaultAsync(s => s.Token == token);
        if (session != null)
        {
            _context.UserSessions.Remove(session);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<bool> LockSessionAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        var session = await _context.UserSessions.FirstOrDefaultAsync(s => s.Token == token);
        if (session == null)
        {
            return false;
        }

        session.IsLocked = true;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task UnlockSessionAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;

        var session = await _context.UserSessions.FirstOrDefaultAsync(s => s.Token == token);
        if (session != null)
        {
            session.IsLocked = false;
            await _context.SaveChangesAsync();
        }
    }

    public async Task<List<SessionSummary>> GetSessionsAsync(string username, string? currentToken)
    {
        var expired = await _context.UserSessions
            .Where(s => s.Username == username && s.ExpiresAt < DateTime.UtcNow)
            .ToListAsync();
        if (expired.Count > 0)
        {
            _context.UserSessions.RemoveRange(expired);
            await _context.SaveChangesAsync();
        }

        return await _context.UserSessions
            .Where(s => s.Username == username)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new SessionSummary(
                s.Id,
                s.DeviceName,
                s.CreatedAt,
                s.ExpiresAt,
                s.IsLocked,
                s.LastActiveAt,
                s.IpAddress,
                s.UserAgent,
                s.Token == currentToken))
            .ToListAsync();
    }

    public async Task HeartbeatAsync(string? token, string? ipAddress, string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(token)) return;

        var session = await _context.UserSessions.FirstOrDefaultAsync(s => s.Token == token);
        if (session != null)
        {
            session.LastActiveAt = DateTime.UtcNow;
            session.IpAddress = ipAddress;
            session.UserAgent = userAgent;
            await _context.SaveChangesAsync();
        }
    }

    public async Task RevokeSessionAsync(string username, Guid id)
    {
        var session = await _context.UserSessions
            .FirstOrDefaultAsync(s => s.Id == id && s.Username == username);

        if (session != null)
        {
            _context.UserSessions.Remove(session);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<int> RevokeAllSessionsAsync(string username, string? currentToken, bool keepCurrent)
    {
        var sessions = await _context.UserSessions
            .Where(s => s.Username == username && (!keepCurrent || s.Token != currentToken))
            .ToListAsync();

        _context.UserSessions.RemoveRange(sessions);
        await _context.SaveChangesAsync();

        return sessions.Count;
    }

    public async Task<int> RevokeOtherSessionsAsync(string username, string? currentToken)
    {
        var otherSessions = await _context.UserSessions
            .Where(s => s.Username == username && s.Token != currentToken)
            .ToListAsync();
        _context.UserSessions.RemoveRange(otherSessions);
        await _context.SaveChangesAsync();
        return otherSessions.Count;
    }
}
