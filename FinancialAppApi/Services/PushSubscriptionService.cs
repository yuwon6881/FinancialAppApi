using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public sealed record PushStatusResult(bool AccountEnabled, bool DeviceSubscribed);

public class PushSubscriptionService
{
    private readonly AppDbContext _context;
    private readonly TimeProvider _timeProvider;

    public PushSubscriptionService(AppDbContext context, TimeProvider? timeProvider = null)
    {
        _context = context;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PushStatusResult> GetStatusAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        var accountEnabled = setting?.PushRemindersEnabled ?? false;

        var deviceSubscribed = false;
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            deviceSubscribed = await _context.PushSubscriptions
                .AnyAsync(s => s.DeviceId == deviceId && s.Enabled, cancellationToken);
        }

        return new PushStatusResult(accountEnabled, deviceSubscribed);
    }

    public async Task<bool> SetAccountEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        if (setting == null)
        {
            return false;
        }

        setting.PushRemindersEnabled = enabled;
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<PushSubscription> SubscribeAsync(string deviceId, string fcmToken, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Re-registering an existing device replaces its token/enabled state in place rather
        // than accumulating a duplicate row (also enforced by the unique user+device index).
        var existing = await _context.PushSubscriptions.FirstOrDefaultAsync(s => s.DeviceId == deviceId, cancellationToken);
        if (existing != null)
        {
            existing.FcmToken = fcmToken;
            existing.Enabled = true;
            existing.UpdatedAt = now;
            await _context.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var subscription = new PushSubscription
        {
            Id = $"push-{Guid.NewGuid():N}",
            DeviceId = deviceId,
            FcmToken = fcmToken,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.PushSubscriptions.Add(subscription);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return subscription;
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Two tabs can register the same browser device concurrently. The unique
            // (user, device) index decides the winner; converge the losing request onto that
            // row instead of returning a transient 500 to the client.
            _context.Entry(subscription).State = EntityState.Detached;
            var winner = await _context.PushSubscriptions
                .FirstAsync(s => s.DeviceId == deviceId, cancellationToken);
            winner.FcmToken = fcmToken;
            winner.Enabled = true;
            winner.UpdatedAt = now;
            await _context.SaveChangesAsync(cancellationToken);
            return winner;
        }
    }

    public async Task<bool> UnsubscribeAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var existing = await _context.PushSubscriptions.FirstOrDefaultAsync(s => s.DeviceId == deviceId, cancellationToken);
        if (existing == null)
        {
            return false;
        }

        _context.PushSubscriptions.Remove(existing);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
