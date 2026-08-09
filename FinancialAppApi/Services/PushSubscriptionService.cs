using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public sealed record PushStatusResult(bool AccountEnabled, bool DeviceSubscribed, bool CategoryAlertsEnabled);

public sealed record PushDeviceSummary(string Id, bool IsCurrent, DateTime CreatedAt, DateTime UpdatedAt);

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
        // Live device subscriptions are the single source of truth for "is this account opted in":
        // there is no separate account-level flag to disagree with them.
        var hasEnabledSubscription = await _context.PushSubscriptions
            .AnyAsync(s => s.Enabled, cancellationToken);
        var accountEnabled = hasEnabledSubscription;

        var deviceSubscribed = false;
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            deviceSubscribed = await _context.PushSubscriptions
                .AnyAsync(s => s.DeviceId == deviceId && s.Enabled, cancellationToken);
        }

        var categoryAlertsEnabled = await _context.FinancialSettings
            .Select(setting => setting.CategoryLimitAlertsEnabled)
            .FirstOrDefaultAsync(cancellationToken);

        return new PushStatusResult(accountEnabled, deviceSubscribed, categoryAlertsEnabled);
    }

    // The account's opted-in devices, so the user can see and revoke an enrolment made on a
    // browser they no longer have in front of them. Only the opaque row id is exposed -- the
    // device id is a client-generated correlation key and the FCM token is a send credential;
    // neither belongs in a list rendered on screen.
    public async Task<IReadOnlyList<PushDeviceSummary>> GetDevicesAsync(
        string? currentDeviceId,
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.PushSubscriptions
            .Where(s => s.Enabled)
            .OrderByDescending(s => s.UpdatedAt)
            .ThenBy(s => s.Id)
            .Select(s => new { s.Id, s.DeviceId, s.CreatedAt, s.UpdatedAt })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new PushDeviceSummary(
                row.Id,
                !string.IsNullOrWhiteSpace(currentDeviceId) && row.DeviceId == currentDeviceId,
                row.CreatedAt,
                row.UpdatedAt))
            .ToList();
    }

    public async Task<bool> SetCategoryAlertsEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        if (setting == null) return false;

        if (enabled && !await _context.PushSubscriptions.AnyAsync(item => item.Enabled, cancellationToken))
        {
            return false;
        }

        setting.CategoryLimitAlertsEnabled = enabled;
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

    public Task<bool> UnsubscribeAsync(string deviceId, CancellationToken cancellationToken = default) =>
        DisableAsync(s => s.DeviceId == deviceId, cancellationToken);

    public Task<bool> RevokeDeviceAsync(string subscriptionId, CancellationToken cancellationToken = default) =>
        DisableAsync(s => s.Id == subscriptionId, cancellationToken);

    // Opting a device out disables the row rather than deleting it. Both delivery ledgers
    // (PushReminderDelivery, CategoryLimitAlertDelivery) claim their "already sent" rows against
    // PushSubscription.Id, so deleting the row and minting a new id on re-subscribe orphaned
    // every claim -- a reminder already delivered today would be sent again after an off/on
    // toggle. Keeping the row keeps the claims addressable, and SubscribeAsync re-enables it.
    private async Task<bool> DisableAsync(
        System.Linq.Expressions.Expression<Func<PushSubscription, bool>> predicate,
        CancellationToken cancellationToken)
    {
        var existing = await _context.PushSubscriptions.FirstOrDefaultAsync(predicate, cancellationToken);
        if (existing == null)
        {
            return false;
        }

        existing.Enabled = false;
        // A revoked device must not keep a live send credential on file; re-subscribing always
        // supplies a fresh token.
        existing.FcmToken = string.Empty;
        existing.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

        var hasAnotherEnabledDevice = await _context.PushSubscriptions
            .AnyAsync(s => s.Id != existing.Id && s.Enabled, cancellationToken);
        if (!hasAnotherEnabledDevice)
        {
            // Category alerts cannot be switched on without a device (SetCategoryAlertsEnabledAsync
            // refuses), so leaving the consent on when the last device leaves creates a state the
            // user cannot reach deliberately -- and it is the state where every once-per-cycle
            // milestone would be spent on an alert nobody can receive.
            var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
            if (setting != null)
            {
                setting.CategoryLimitAlertsEnabled = false;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
