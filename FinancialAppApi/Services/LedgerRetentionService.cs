using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public sealed record LedgerRetentionResult(
    int PriceBars,
    int ReminderDeliveries,
    int AlertEvents,
    int AlertDeliveries,
    int AlertEvaluations,
    int OrphanedMilestones)
{
    public int Total => PriceBars
        + ReminderDeliveries
        + AlertEvents
        + AlertDeliveries
        + AlertEvaluations
        + OrphanedMilestones;
}

/// <summary>
/// Prunes the append-only bookkeeping tables described by <see cref="LedgerRetentionPolicy"/>.
/// <para>
/// Every delete here is of derived or transient state -- market price history that can be refetched,
/// and notification receipts that only exist to make delivery idempotent. No financial record, and
/// nothing a report reads, is touched.
/// </para>
/// </summary>
public sealed class LedgerRetentionService
{
    private readonly AppDbContext _context;
    private readonly LedgerRetentionPolicy _policy;
    private readonly FinancialClock _clock;

    public LedgerRetentionService(
        AppDbContext context,
        LedgerRetentionPolicy policy,
        FinancialClock clock)
    {
        _context = context;
        _policy = policy;
        _clock = clock;
    }

    public async Task<LedgerRetentionResult> PruneAsync(CancellationToken cancellationToken = default)
    {
        if (!_policy.Enabled || !_context.Database.IsRelational())
        {
            return new LedgerRetentionResult(0, 0, 0, 0, 0, 0);
        }

        var today = _clock.Today;
        var priceBarCutoff = today.AddDays(-_policy.PriceBarRetentionDays);
        var notificationCutoff = DateTime.UtcNow.AddDays(-_policy.NotificationRetentionDays);

        // MarketPriceBars is a shared provider cache with no owner, so it carries no query filter.
        var priceBars = await _context.MarketPriceBars
            .Where(bar => bar.MarketDate < priceBarCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        // The rest are user-owned. This runs outside any request, so the tenancy filter has no
        // current user to resolve and must be bypassed explicitly -- one of the narrow background
        // exceptions the fail-closed rule allows.
        var reminderDeliveries = await _context.PushReminderDeliveries
            .IgnoreQueryFilters()
            .Where(delivery => delivery.SentAt < notificationCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var alertDeliveries = await _context.CategoryLimitAlertDeliveries
            .IgnoreQueryFilters()
            .Where(delivery => delivery.SentAt < notificationCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var alertEvaluations = await _context.CategoryLimitAlertEvaluations
            .IgnoreQueryFilters()
            .Where(evaluation => evaluation.CreatedAt < notificationCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        // Only settled events. A still-open one is pending work no matter how old it looks.
        var alertEvents = await _context.CategoryLimitAlertEvents
            .IgnoreQueryFilters()
            .Where(alertEvent => alertEvent.CompletedAt != null && alertEvent.CreatedAt < notificationCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        // Milestones record "this threshold was already announced" and carry no timestamp of their
        // own, so they are pruned by the event they belong to rather than by age.
        var orphanedMilestones = await _context.CategoryLimitAlertMilestones
            .IgnoreQueryFilters()
            .Where(milestone => !_context.CategoryLimitAlertEvents
                .IgnoreQueryFilters()
                .Any(alertEvent => alertEvent.Id == milestone.EventId))
            .ExecuteDeleteAsync(cancellationToken);

        return new LedgerRetentionResult(
            priceBars,
            reminderDeliveries,
            alertEvents,
            alertDeliveries,
            alertEvaluations,
            orphanedMilestones);
    }
}
