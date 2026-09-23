using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

// The two notification kinds, named once. Both the request DTO and the service speak in these
// rather than in a pair of booleans, so no call site can silently transpose them.
public static class PushChannel
{
    public const string BillReminders = "billReminders";
    public const string CategoryAlerts = "categoryAlerts";

    public static bool IsKnown(string? channel) =>
        string.Equals(channel, BillReminders, StringComparison.OrdinalIgnoreCase)
        || string.Equals(channel, CategoryAlerts, StringComparison.OrdinalIgnoreCase);

    public static bool IsBillReminders(string channel) =>
        string.Equals(channel, BillReminders, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What one client should render for one device.
/// </summary>
/// <remarks>
/// Every field is scoped, and the scopes are the whole point. <c>ThisDevice*</c> is what this
/// browser will actually receive; <c>OtherDevices*</c> is whether some *other* device is opted in.
/// They used to be one account-wide flag for spending alerts, so a desktop that had never asked
/// for anything rendered the switch "on" because a phone had -- a claim the desktop could not
/// honour, since delivery has always been per registered device.
/// </remarks>
public sealed record PushStatusResult(
    bool ThisDeviceBillReminders,
    bool ThisDeviceCategoryAlerts,
    bool ThisDeviceShowNotificationDetails,
    bool OtherDevicesBillReminders,
    bool OtherDevicesCategoryAlerts,
    bool TokenRenewalRequired)
{
    public bool DeviceSubscribed => ThisDeviceBillReminders || ThisDeviceCategoryAlerts;
    public bool AccountEnabled => DeviceSubscribed || OtherDevicesBillReminders || OtherDevicesCategoryAlerts;
}

public sealed record PushDeviceSummary(
    string Id,
    bool IsCurrent,
    bool BillReminders,
    bool CategoryAlerts,
    DateTime CreatedAt,
    DateTime UpdatedAt);

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
        // Live device subscriptions are the single source of truth for every part of this answer:
        // there is no separate account-level flag to disagree with them.
        var rows = await _context.PushSubscriptions
            .Select(s => new
            {
                s.DeviceId,
                s.Enabled,
                TokenMissing = s.FcmToken == string.Empty,
                s.BillRemindersEnabled,
                s.CategoryAlertsEnabled,
                s.ShowNotificationDetails
            })
            .ToListAsync(cancellationToken);

        var thisDeviceBills = false;
        var thisDeviceAlerts = false;
        var thisDeviceShowDetails = false;
        var otherBills = false;
        var otherAlerts = false;
        var tokenRenewalRequired = false;
        foreach (var row in rows)
        {
            var isThisDevice = !string.IsNullOrWhiteSpace(deviceId) && row.DeviceId == deviceId;
            if (isThisDevice && !row.Enabled && row.TokenMissing)
            {
                tokenRenewalRequired = true;
            }
            if (isThisDevice)
            {
                thisDeviceShowDetails |= row.ShowNotificationDetails;
            }
            if (!row.Enabled) continue;
            if (isThisDevice)
            {
                thisDeviceBills |= row.BillRemindersEnabled;
                thisDeviceAlerts |= row.CategoryAlertsEnabled;
            }
            else
            {
                otherBills |= row.BillRemindersEnabled;
                otherAlerts |= row.CategoryAlertsEnabled;
            }
        }

        return new PushStatusResult(
            thisDeviceBills,
            thisDeviceAlerts,
            thisDeviceShowDetails,
            otherBills,
            otherAlerts,
            tokenRenewalRequired);
    }

    public async Task<bool> SetShowNotificationDetailsAsync(
        string deviceId,
        bool showDetails,
        CancellationToken cancellationToken = default)
    {
        var existing = await _context.PushSubscriptions
            .FirstOrDefaultAsync(s => s.DeviceId == deviceId, cancellationToken);
        if (existing == null) return false;

        existing.ShowNotificationDetails = showDetails;
        existing.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // The account's opted-in devices, so the user can see and revoke an enrolment made on a
    // browser they no longer have in front of them, and see which kinds each one receives. Only
    // the opaque row id is exposed -- the device id is a client-generated correlation key and the
    // FCM token is a send credential; neither belongs in a list rendered on screen.
    public async Task<IReadOnlyList<PushDeviceSummary>> GetDevicesAsync(
        string? currentDeviceId,
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.PushSubscriptions
            .Where(s => s.Enabled)
            .OrderByDescending(s => s.UpdatedAt)
            .ThenBy(s => s.Id)
            .Select(s => new { s.Id, s.DeviceId, s.BillRemindersEnabled, s.CategoryAlertsEnabled, s.CreatedAt, s.UpdatedAt })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new PushDeviceSummary(
                row.Id,
                !string.IsNullOrWhiteSpace(currentDeviceId) && row.DeviceId == currentDeviceId,
                row.BillRemindersEnabled,
                row.CategoryAlertsEnabled,
                row.CreatedAt,
                row.UpdatedAt))
            .ToList();
    }

    /// <summary>
    /// Registers or updates this device's channel opt-ins against a live FCM token.
    /// </summary>
    /// <remarks>
    /// A null channel flag means "leave whatever this device already chose alone", so turning on
    /// one kind can never quietly turn on the other. Turning the last kind off unsubscribes the
    /// device outright, which is the same state <see cref="UnsubscribeAsync"/> reaches -- the two
    /// switches and the single off action must not be able to leave a registered device that
    /// receives nothing, because the dispatcher would keep it in the fan-out forever.
    /// </remarks>
    public async Task<PushSubscription?> SubscribeAsync(
        string deviceId,
        string fcmToken,
        bool? billReminders = null,
        bool? categoryAlerts = null,
        CancellationToken cancellationToken = default,
        string platform = PushPlatform.Web)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var normalizedPlatform = PushPlatform.Normalize(platform);

        // Re-registering an existing device replaces its token/enabled state in place rather
        // than accumulating a duplicate row (also enforced by the unique user+device index).
        var existing = await _context.PushSubscriptions.FirstOrDefaultAsync(s => s.DeviceId == deviceId, cancellationToken);
        if (existing != null)
        {
            var wantsBills = billReminders ?? (existing.Enabled && existing.BillRemindersEnabled);
            var wantsAlerts = categoryAlerts ?? (existing.Enabled && existing.CategoryAlertsEnabled);
            if (!wantsBills && !wantsAlerts)
            {
                await DisableRowAsync(existing, now, cancellationToken);
                return null;
            }

            existing.FcmToken = fcmToken;
            existing.Platform = normalizedPlatform;
            existing.Enabled = true;
            existing.BillRemindersEnabled = wantsBills;
            existing.CategoryAlertsEnabled = wantsAlerts;
            existing.UpdatedAt = now;
            await SyncCategoryAlertConsentAsync(cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            return existing;
        }

        // A brand-new device with no stated channels registers for bill reminders only: spending
        // alerts have always been the deliberate second opt-in, and defaulting them on would opt
        // a device into notifications its user never asked for.
        var newBills = billReminders ?? true;
        var newAlerts = categoryAlerts ?? false;
        if (!newBills && !newAlerts) return null;

        var subscription = new PushSubscription
        {
            Id = $"push-{Guid.NewGuid():N}",
            DeviceId = deviceId,
            FcmToken = fcmToken,
            Platform = normalizedPlatform,
            Enabled = true,
            BillRemindersEnabled = newBills,
            CategoryAlertsEnabled = newAlerts,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.PushSubscriptions.Add(subscription);
        try
        {
            await SyncCategoryAlertConsentAsync(cancellationToken);
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
            winner.Platform = normalizedPlatform;
            winner.Enabled = true;
            winner.BillRemindersEnabled = newBills || winner.BillRemindersEnabled;
            winner.CategoryAlertsEnabled = newAlerts || winner.CategoryAlertsEnabled;
            winner.UpdatedAt = now;
            await SyncCategoryAlertConsentAsync(cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            return winner;
        }
    }

    /// <summary>
    /// Turns one channel off for one device without needing a fresh FCM token.
    /// </summary>
    /// <remarks>
    /// Turning a channel *on* always goes through <see cref="SubscribeAsync"/>, because the
    /// client has to prove browser permission and produce a token first; there is deliberately no
    /// path here that can enable a channel from a stale token.
    /// </remarks>
    public async Task<bool> DisableChannelAsync(
        string deviceId,
        string channel,
        CancellationToken cancellationToken = default)
    {
        var existing = await _context.PushSubscriptions
            .FirstOrDefaultAsync(s => s.DeviceId == deviceId, cancellationToken);
        if (existing == null || !existing.Enabled) return false;

        if (PushChannel.IsBillReminders(channel)) existing.BillRemindersEnabled = false;
        else existing.CategoryAlertsEnabled = false;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (!existing.BillRemindersEnabled && !existing.CategoryAlertsEnabled)
        {
            await DisableRowAsync(existing, now, cancellationToken);
            return true;
        }

        existing.UpdatedAt = now;
        await SyncCategoryAlertConsentAsync(cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task<bool> UnsubscribeAsync(string deviceId, CancellationToken cancellationToken = default) =>
        DisableAsync(s => s.DeviceId == deviceId, cancellationToken);

    public Task<bool> RevokeDeviceAsync(string subscriptionId, CancellationToken cancellationToken = default) =>
        DisableAsync(s => s.Id == subscriptionId, cancellationToken);

    /// <summary>
    /// Retires a token that FCM has definitively rejected while preserving the durable push work
    /// that can still be delivered after this device renews its registration.
    /// </summary>
    public async Task RetireInvalidTokenAsync(
        PushSubscription subscription,
        CancellationToken cancellationToken = default)
    {
        subscription.Enabled = false;
        subscription.BillRemindersEnabled = false;
        subscription.CategoryAlertsEnabled = false;
        subscription.FcmToken = string.Empty;
        subscription.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

        // This must inspect tracked values as well as stored rows. A database AnyAsync here sees
        // this subscription's pre-save flags and leaves the account mirror incorrectly enabled.
        await SyncCategoryAlertConsentAsync(cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> DisableAsync(
        System.Linq.Expressions.Expression<Func<PushSubscription, bool>> predicate,
        CancellationToken cancellationToken)
    {
        var existing = await _context.PushSubscriptions.FirstOrDefaultAsync(predicate, cancellationToken);
        if (existing == null)
        {
            return false;
        }

        await DisableRowAsync(existing, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        return true;
    }

    // Opting a device out disables the row rather than deleting it. Both delivery ledgers
    // (PushReminderDelivery, CategoryLimitAlertDelivery) claim their "already sent" rows against
    // PushSubscription.Id, so deleting the row and minting a new id on re-subscribe orphaned
    // every claim -- a reminder already delivered today would be sent again after an off/on
    // toggle. Keeping the row keeps the claims addressable, and SubscribeAsync re-enables it.
    private async Task DisableRowAsync(PushSubscription existing, DateTime now, CancellationToken cancellationToken)
    {
        existing.Enabled = false;
        existing.BillRemindersEnabled = false;
        existing.CategoryAlertsEnabled = false;
        // A revoked device must not keep a live send credential on file; re-subscribing always
        // supplies a fresh token.
        existing.FcmToken = string.Empty;
        existing.UpdatedAt = now;

        await SyncCategoryAlertConsentAsync(cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Keeps <see cref="FinancialSetting.CategoryLimitAlertsEnabled"/> equal to "some enabled
    /// device wants spending alerts".
    /// </summary>
    /// <remarks>
    /// That column is a **derived mirror**, not a second opinion. It exists because
    /// <c>AppDbContext.CaptureCategoryLimitAlertEvaluations</c> runs inside SaveChanges, where it
    /// can only read an already-tracked entity -- it cannot query for subscription rows -- and
    /// skipping the capture for an account that wants no alerts is what keeps every ledger write
    /// from paying for evaluation rows nobody will read. Delivery and the milestone latch check
    /// the per-device flags; this only ever answers "is it worth capturing at all".
    ///
    /// Call this *before* SaveChanges on the same context so consent and the rows it is derived
    /// from commit together.
    /// </remarks>
    private async Task SyncCategoryAlertConsentAsync(CancellationToken cancellationToken)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        if (setting == null) return;

        // Evaluated in memory over stored *and* tracked rows: a store query cannot see an Added
        // row, and a row already modified in this save would be judged on its old column value.
        // An account has a handful of devices, so materializing them costs nothing here.
        var stored = await _context.PushSubscriptions.ToListAsync(cancellationToken);
        var wanted = stored
            .Concat(_context.PushSubscriptions.Local)
            .Any(s => s.Enabled && s.CategoryAlertsEnabled);

        if (setting.CategoryLimitAlertsEnabled != wanted)
        {
            setting.CategoryLimitAlertsEnabled = wanted;
        }
    }
}
