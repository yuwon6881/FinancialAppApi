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
        byte[]? credentialId = null,
        CancellationToken cancellationToken = default)
    {
        _context.SetCurrentUser(user.Id);
        var expiredSessions = await _context.UserSessions
            .Where(s => s.UserId == user.Id && s.ExpiresAt < DateTime.UtcNow)
            .ToListAsync(cancellationToken);
        if (expiredSessions.Count > 0)
        {
            _context.UserSessions.RemoveRange(expiredSessions);
        }

        if (credentialId != null)
        {
            var priorSessionsForCredential = await _context.UserSessions
                .Where(s => s.UserId == user.Id && s.CredentialId != null && s.CredentialId == credentialId)
                .ToListAsync(cancellationToken);
            if (priorSessionsForCredential.Count > 0)
            {
                _context.UserSessions.RemoveRange(priorSessionsForCredential);
            }
        }

        if (!string.IsNullOrEmpty(deviceId))
        {
            var deviceSessions = await _context.UserSessions
                .Where(s => s.UserId == user.Id && s.DeviceId == deviceId)
                .ToListAsync(cancellationToken);
            if (deviceSessions.Count > 0)
            {
                _context.UserSessions.RemoveRange(deviceSessions);
            }
        }
        else if (credentialId == null)
        {
            var oldPasswordSessions = await _context.UserSessions
                .Where(s => s.UserId == user.Id && s.CredentialId == null)
                .ToListAsync(cancellationToken);
            if (oldPasswordSessions.Count > 0)
            {
                _context.UserSessions.RemoveRange(oldPasswordSessions);
            }
        }

        var activeSessions = await _context.UserSessions
            .Where(s => s.UserId == user.Id)
            .ToListAsync(cancellationToken);
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
            UserId = user.Id,
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
        await SaveSessionWithConcurrentPruneToleranceAsync(cancellationToken);

        return session;
    }

    public async Task LogoutAsync(
        string? token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return;

        var session = await _context.UserSessions
            .FirstOrDefaultAsync(s => s.Token == token, cancellationToken);
        if (session != null)
        {
            _context.UserSessions.Remove(session);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<bool> LockSessionAsync(
        string? token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        // An inactivity lock is issued only after 5 minutes of idle time. If the session was
        // actively used or unlocked recently (within the last 4 minutes), reject the lock request
        // as a stale in-flight race against user activity or unlock.
        var recentThreshold = DateTime.UtcNow.AddMinutes(-4);

        if (_context.Database.IsRelational())
        {
            var updated = await _context.UserSessions
                .Where(s => s.Token == token && (!s.LastActiveAt.HasValue || s.LastActiveAt.Value <= recentThreshold))
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.IsLocked, true), cancellationToken);
            return updated > 0;
        }

        var session = await _context.UserSessions
            .FirstOrDefaultAsync(s => s.Token == token, cancellationToken);
        if (session == null)
        {
            return false;
        }

        if (session.LastActiveAt.HasValue && session.LastActiveAt.Value > recentThreshold)
        {
            return false;
        }

        session.IsLocked = true;
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task UnlockSessionAsync(
        string? token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return;

        var now = DateTime.UtcNow;
        if (_context.Database.IsRelational())
        {
            await _context.UserSessions
                .Where(s => s.Token == token)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.IsLocked, false)
                    .SetProperty(b => b.LastActiveAt, now), cancellationToken);
            return;
        }

        var session = await _context.UserSessions
            .FirstOrDefaultAsync(s => s.Token == token, cancellationToken);
        if (session != null)
        {
            session.IsLocked = false;
            session.LastActiveAt = now;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<List<SessionSummary>> GetSessionsAsync(
        string username,
        string? currentToken,
        CancellationToken cancellationToken = default)
    {
        var userId = _context.RequireCurrentUserId();
        var expired = await _context.UserSessions
            .Where(s => s.UserId == userId && s.ExpiresAt < DateTime.UtcNow)
            .ToListAsync(cancellationToken);
        if (expired.Count > 0)
        {
            _context.UserSessions.RemoveRange(expired);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                foreach (var entry in exception.Entries.Where(e => e.State == EntityState.Deleted))
                    entry.State = EntityState.Detached;
            }
        }

        return await _context.UserSessions
            .Where(s => s.UserId == userId)
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
            .ToListAsync(cancellationToken);
    }

    public async Task HeartbeatAsync(
        string? token,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return;

        var session = await _context.UserSessions
            .FirstOrDefaultAsync(s => s.Token == token, cancellationToken);
        if (session != null)
        {
            session.LastActiveAt = DateTime.UtcNow;
            session.IpAddress = ipAddress;
            session.UserAgent = userAgent;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RevokeSessionAsync(
        string username,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var userId = _context.RequireCurrentUserId();
        var session = await _context.UserSessions
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, cancellationToken);

        if (session != null)
        {
            _context.UserSessions.Remove(session);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<int> RevokeAllSessionsAsync(
        string username,
        string? currentToken,
        bool keepCurrent,
        CancellationToken cancellationToken = default)
    {
        if (keepCurrent && string.IsNullOrWhiteSpace(currentToken)) return 0;

        var userId = _context.RequireCurrentUserId();
        var sessions = await _context.UserSessions
            .Where(s => s.UserId == userId && (!keepCurrent || s.Token != currentToken))
            .ToListAsync(cancellationToken);

        _context.UserSessions.RemoveRange(sessions);
        await _context.SaveChangesAsync(cancellationToken);

        return sessions.Count;
    }

    // Revokes every session for a specific user by id, without requiring an authenticated
    // database scope. Used by unauthenticated flows (e.g. password recovery) that have
    // already verified the account out-of-band. UserSession is not query-filtered, so this
    // is safe with a null CurrentUserId.
    public async Task<int> RevokeAllSessionsForUserAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var sessions = await _context.UserSessions
            .Where(s => s.UserId == userId)
            .ToListAsync(cancellationToken);

        _context.UserSessions.RemoveRange(sessions);
        await _context.SaveChangesAsync(cancellationToken);

        return sessions.Count;
    }

    public async Task<int> RevokeOtherSessionsAsync(
        string username,
        string? currentToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentToken)) return 0;

        var userId = _context.RequireCurrentUserId();
        var otherSessions = await _context.UserSessions
            .Where(s => s.UserId == userId && s.Token != currentToken)
            .ToListAsync(cancellationToken);
        _context.UserSessions.RemoveRange(otherSessions);
        await _context.SaveChangesAsync(cancellationToken);
        return otherSessions.Count;
    }

    private async Task SaveSessionWithConcurrentPruneToleranceAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            var deletedEntries = exception.Entries
                .Where(e => e.State == EntityState.Deleted && e.Metadata.ClrType == typeof(UserSession))
                .ToList();
            if (deletedEntries.Count == 0 || deletedEntries.Count != exception.Entries.Count) throw;
            foreach (var entry in deletedEntries) entry.State = EntityState.Detached;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
