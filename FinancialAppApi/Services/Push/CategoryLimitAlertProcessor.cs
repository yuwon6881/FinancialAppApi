using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Push;

public sealed record CategoryLimitAlertDispatchSummary(int Sent, int Skipped, int Disabled);

// Converts durable ledger-change evaluations into at most two alerts per category/cycle, then
// delivers pending events through the same FCM transport as recurring-payment reminders.
public sealed class CategoryLimitAlertProcessor
{
    private const decimal NearRatio = 0.80m;

    private readonly AppDbContext _context;
    private readonly IFcmPushSender _fcmSender;
    private readonly FinancialClock _financialClock;
    private readonly ILogger<CategoryLimitAlertProcessor> _logger;

    public CategoryLimitAlertProcessor(
        AppDbContext context,
        IFcmPushSender fcmSender,
        FinancialClock financialClock,
        ILogger<CategoryLimitAlertProcessor> logger)
    {
        _context = context;
        _fcmSender = fcmSender;
        _financialClock = financialClock;
        _logger = logger;
    }

    public async Task<CategoryLimitAlertDispatchSummary> ProcessPendingAsync(
        CancellationToken cancellationToken = default)
    {
        await EvaluatePendingChangesAsync(cancellationToken);
        return await DispatchPendingEventsAsync(cancellationToken);
    }

    private async Task EvaluatePendingChangesAsync(CancellationToken cancellationToken)
    {
        var evaluations = await _context.CategoryLimitAlertEvaluations
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        if (evaluations.Count == 0) return;

        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        // The milestone rows written below are the once-per-cycle latch, and they are claimed
        // here rather than at delivery. So this must also refuse to run when there is no device
        // that could receive the alert: otherwise every crossing silently spends its milestone
        // against an event the dispatcher will immediately discard, and a user who turns push on
        // later in the cycle never hears about a category that already crossed.
        var canDeliver = await _context.PushSubscriptions
            .AnyAsync(item => item.Enabled && item.CategoryAlertsEnabled, cancellationToken);
        if (setting == null || !setting.CategoryLimitAlertsEnabled || !canDeliver)
        {
            _context.CategoryLimitAlertEvaluations.RemoveRange(evaluations);
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        var (cycleYear, cycleMonth) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            _financialClock.Today,
            setting.CycleDay);
        var cycleKey = $"{cycleYear:D4}-{cycleMonth:D2}";
        var deltas = BuildCurrentCycleDeltas(evaluations, cycleYear, cycleMonth, setting.CycleDay);

        if (deltas.Count == 0)
        {
            _context.CategoryLimitAlertEvaluations.RemoveRange(evaluations);
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        var categoryNames = deltas.Keys.ToList();
        var categoryTypes = (await _context.TransactionCategories
                .AsNoTracking()
                .Where(category => categoryNames.Contains(category.Name))
                .Select(category => new { category.Name, category.Type })
                .ToListAsync(cancellationToken))
            .ToDictionary(category => category.Name, category => category.Type, StringComparer.OrdinalIgnoreCase);
        var guideVersions = await _context.CategorySpendingGuides
            .AsNoTracking()
            .Where(guide => categoryNames.Contains(guide.CategoryName) &&
                string.Compare(guide.EffectiveFromCycleKey, cycleKey) <= 0)
            .ToListAsync(cancellationToken);
        var limits = guideVersions
            .GroupBy(guide => guide.CategoryName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(guide => guide.EffectiveFromCycleKey).First())
            .Where(guide => guide.LimitAmount.HasValue &&
                categoryTypes.TryGetValue(guide.CategoryName, out var type) &&
                CategoryFlowType.AllowsSpendingGuide(type))
            .ToDictionary(guide => guide.CategoryName, guide => guide.LimitAmount!.Value, StringComparer.OrdinalIgnoreCase);

        var (cycleStart, cycleEnd, _) = CategoryAttributionService.GetCycleRange(
            cycleYear,
            cycleMonth,
            setting.CycleDay);
        var rangeStart = TransactionDate.StartOfDate(DateOnly.FromDateTime(cycleStart));
        var rangeEnd = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(cycleEnd));
        var currentRows = await _context.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.Date >= rangeStart && transaction.Date < rangeEnd && transaction.Amount < 0)
            .ToListAsync(cancellationToken);
        var currentSpent = currentRows
            .Where(IsCountedExpense)
            .Where(transaction => categoryNames.Contains(transaction.Category, StringComparer.OrdinalIgnoreCase))
            .GroupBy(transaction => transaction.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => Math.Abs(group.Sum(transaction => transaction.Amount)),
                StringComparer.OrdinalIgnoreCase);

        var crossings = new List<CategoryLimitCrossing>();
        foreach (var (category, delta) in deltas)
        {
            if (delta <= 0 || !limits.TryGetValue(category, out var limit) || limit <= 0) continue;

            var after = currentSpent.GetValueOrDefault(category);
            var before = Math.Max(0m, after - delta);
            if (before < limit && after >= limit)
            {
                crossings.Add(new CategoryLimitCrossing(category, "Limit", after > limit));
            }
            else if (before < limit * NearRatio && after >= limit * NearRatio && after < limit)
            {
                crossings.Add(new CategoryLimitCrossing(category, "Near", false));
            }
        }

        _context.CategoryLimitAlertEvaluations.RemoveRange(evaluations);
        if (crossings.Count == 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        var existingMilestones = (await _context.CategoryLimitAlertMilestones
                .AsNoTracking()
                .Where(item => item.CycleKey == cycleKey)
                .Select(item => new { item.CategoryName, item.Milestone })
                .ToListAsync(cancellationToken))
            .Select(item => MilestoneKey(item.CategoryName, item.Milestone))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        crossings = crossings
            .Where(item => !existingMilestones.Contains(MilestoneKey(item.CategoryName, item.Milestone)))
            .ToList();
        if (crossings.Count == 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        var now = DateTime.UtcNow;
        var eventId = $"clae-{Guid.NewGuid():N}";
        var content = BuildEventContent(crossings, cycleKey);
        // Expire at the end of the local day the crossing happened on, the same rule the
        // recurring reminder TTL uses. A flat 24h window let an alert about "this cycle so far"
        // arrive the following day, after the figure it described had already moved on.
        var endOfLocalDay = _financialClock.Today.ToDateTime(TimeOnly.MinValue).AddDays(1);
        var expiresAt = now.AddMinutes(Math.Max(1d, (endOfLocalDay - _financialClock.LocalNow).TotalMinutes));
        _context.CategoryLimitAlertEvents.Add(new CategoryLimitAlertEvent
        {
            Id = eventId,
            CycleKey = cycleKey,
            Title = content.Title,
            Body = content.Body,
            Tag = content.Tag,
            CategoryName = content.CategoryName,
            CreatedAt = now,
            ExpiresAt = expiresAt
        });
        foreach (var crossing in crossings)
        {
            _context.CategoryLimitAlertMilestones.Add(new CategoryLimitAlertMilestone
            {
                Id = $"clam-{Guid.NewGuid():N}",
                EventId = eventId,
                CycleKey = cycleKey,
                CategoryName = crossing.CategoryName,
                Milestone = crossing.Milestone
            });
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // A concurrent transaction claimed the same category milestone. Its event owns the
            // notification; the ledger mutation itself has already committed independently.
            _context.ChangeTracker.Clear();
        }
    }

    private async Task<CategoryLimitAlertDispatchSummary> DispatchPendingEventsAsync(
        CancellationToken cancellationToken)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        var events = await _context.CategoryLimitAlertEvents
            .Where(item => item.CompletedAt == null)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        if (events.Count == 0) return new CategoryLimitAlertDispatchSummary(0, 0, 0);

        var now = DateTime.UtcNow;
        if (setting == null || !setting.CategoryLimitAlertsEnabled)
        {
            await ReleaseMilestonesAsync(events.Select(item => item.Id), cancellationToken);
            foreach (var item in events) item.CompletedAt = now;
            await _context.SaveChangesAsync(cancellationToken);
            return new CategoryLimitAlertDispatchSummary(0, events.Count, 0);
        }

        // Only the devices that asked for spending alerts. A phone opted into these while the
        // desktop asked for bill reminders only means exactly one device receives this.
        var subscriptions = await _context.PushSubscriptions
            .Where(subscription => subscription.Enabled && subscription.CategoryAlertsEnabled)
            .ToListAsync(cancellationToken);
        if (subscriptions.Count == 0)
        {
            await ReleaseMilestonesAsync(events.Select(item => item.Id), cancellationToken);
            foreach (var item in events) item.CompletedAt = now;
            await _context.SaveChangesAsync(cancellationToken);
            return new CategoryLimitAlertDispatchSummary(0, events.Count, 0);
        }

        var sent = 0;
        var skipped = 0;
        var disabled = 0;
        foreach (var alertEvent in events)
        {
            if (alertEvent.ExpiresAt <= now)
            {
                await ReleaseMilestonesAsync([alertEvent.Id], cancellationToken);
                alertEvent.CompletedAt = now;
                skipped++;
                continue;
            }

            // Re-tested per event because a send below can retire a token mid-loop.
            foreach (var subscription in subscriptions.Where(item => item.Enabled && item.CategoryAlertsEnabled))
            {
                if (await _context.CategoryLimitAlertDeliveries.AnyAsync(
                        item => item.EventId == alertEvent.Id && item.SubscriptionId == subscription.Id,
                        cancellationToken))
                {
                    continue;
                }

                var claim = new CategoryLimitAlertDelivery
                {
                    Id = $"clad-{Guid.NewGuid():N}",
                    EventId = alertEvent.Id,
                    SubscriptionId = subscription.Id,
                    SentAt = now
                };
                _context.CategoryLimitAlertDeliveries.Add(claim);
                try
                {
                    await _context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (ex.IsUniqueViolation())
                {
                    _context.Entry(claim).State = EntityState.Detached;
                    skipped++;
                    continue;
                }

                var ttl = alertEvent.ExpiresAt - now;
                var data = new Dictionary<string, string>
                {
                    ["cycleKey"] = alertEvent.CycleKey
                };
                if (!string.IsNullOrWhiteSpace(alertEvent.CategoryName))
                {
                    data["categoryName"] = alertEvent.CategoryName;
                }
                var result = await _fcmSender.SendAsync(
                    subscription.FcmToken,
                    new PushNotificationContent(
                        Kind: "category-limit",
                        Title: alertEvent.Title,
                        Body: alertEvent.Body,
                        Tag: alertEvent.Tag,
                        Route: "/reports?focus=category-limits",
                        TimeToLive: ttl < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : ttl,
                        Data: data),
                    cancellationToken);

                if (result.Status == FcmSendStatus.Sent)
                {
                    sent++;
                }
                else if (result.Status == FcmSendStatus.InvalidOrUnregistered)
                {
                    // Retired token: off for every kind, so the invariant holds and no switch
                    // keeps claiming this device receives something.
                    subscription.Enabled = false;
                    subscription.BillRemindersEnabled = false;
                    subscription.CategoryAlertsEnabled = false;
                    subscription.FcmToken = string.Empty;
                    if (setting != null && !await _context.PushSubscriptions.AnyAsync(
                            candidate => candidate.Enabled && candidate.CategoryAlertsEnabled,
                            cancellationToken))
                    {
                        setting.CategoryLimitAlertsEnabled = false;
                    }
                    disabled++;
                    await _context.SaveChangesAsync(cancellationToken);
                }
                else
                {
                    _context.CategoryLimitAlertDeliveries.Remove(claim);
                    await _context.SaveChangesAsync(cancellationToken);
                    skipped++;
                    _logger.LogWarning("Category limit push event {EventId} could not be sent.", alertEvent.Id);
                }
            }

            var hasUndeliveredEnabledDevice = await _context.PushSubscriptions
                .Where(subscription => subscription.Enabled && subscription.CategoryAlertsEnabled)
                .AnyAsync(subscription => !_context.CategoryLimitAlertDeliveries.Any(delivery =>
                    delivery.EventId == alertEvent.Id && delivery.SubscriptionId == subscription.Id), cancellationToken);
            if (!hasUndeliveredEnabledDevice)
            {
                alertEvent.CompletedAt = now;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return new CategoryLimitAlertDispatchSummary(sent, skipped, disabled);
    }

    private async Task ReleaseMilestonesAsync(
        IEnumerable<string> eventIds,
        CancellationToken cancellationToken)
    {
        var ids = eventIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return;

        var milestones = await _context.CategoryLimitAlertMilestones
            .Where(item => ids.Contains(item.EventId))
            .ToListAsync(cancellationToken);
        _context.CategoryLimitAlertMilestones.RemoveRange(milestones);
    }

    private static Dictionary<string, decimal> BuildCurrentCycleDeltas(
        IEnumerable<CategoryLimitAlertEvaluation> evaluations,
        int cycleYear,
        int cycleMonth,
        int cycleDay)
    {
        var deltas = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in evaluations)
        {
            AddContribution(
                deltas,
                item.PreviousCategory,
                item.PreviousLedgerCategory,
                item.PreviousDate,
                item.PreviousAmount,
                -1m,
                cycleYear,
                cycleMonth,
                cycleDay);
            AddContribution(
                deltas,
                item.CurrentCategory,
                item.CurrentLedgerCategory,
                item.CurrentDate,
                item.CurrentAmount,
                1m,
                cycleYear,
                cycleMonth,
                cycleDay);
        }
        return deltas;
    }

    private static void AddContribution(
        Dictionary<string, decimal> deltas,
        string? category,
        string? ledgerCategory,
        DateTime? date,
        decimal? amount,
        decimal direction,
        int cycleYear,
        int cycleMonth,
        int cycleDay)
    {
        if (string.IsNullOrWhiteSpace(category) || date == null || amount is not < 0 ||
            !IsCountedExpense(category, ledgerCategory)) return;

        var attributed = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            TransactionDate.ToDateOnly(date.Value),
            cycleDay);
        if (attributed.year != cycleYear || attributed.monthIndex != cycleMonth) return;

        deltas[category] = deltas.GetValueOrDefault(category) + Math.Abs(amount.Value) * direction;
    }

    private static bool IsCountedExpense(Transaction transaction) =>
        IsCountedExpense(transaction.Category, transaction.LedgerCategory);

    private static bool IsCountedExpense(string category, string? ledgerCategory) =>
        !string.Equals(category, "Transfer", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(category, "Adjustment", StringComparison.OrdinalIgnoreCase) &&
        !(ledgerCategory?.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase) ?? false);

    private static string MilestoneKey(string category, string milestone) => $"{category}\u001f{milestone}";

    private static (string Title, string Body, string Tag, string? CategoryName) BuildEventContent(
        IReadOnlyList<CategoryLimitCrossing> crossings,
        string cycleKey)
    {
        if (crossings.Count > 1)
        {
            return (
                "Spending guides need attention",
                $"{crossings.Count} categories reached a point you asked to be told about.",
                $"category-limits:{cycleKey}",
                null);
        }

        var crossing = crossings[0];
        var body = crossing.Milestone == "Near"
            ? $"{crossing.CategoryName} is close to what you planned to spend on it this cycle."
            : crossing.Exceeded
                ? $"{crossing.CategoryName} has gone past what you planned to spend on it this cycle."
                : $"{crossing.CategoryName} has reached what you planned to spend on it this cycle.";
        return (
            "Category spending alert",
            body,
            // Same cycle-only tag as the multi-category case (and as the client's
            // buildNotificationTag): later alerts replace earlier ones rather than stacking.
            $"category-limits:{cycleKey}",
            crossing.CategoryName);
    }

    private sealed record CategoryLimitCrossing(string CategoryName, string Milestone, bool Exceeded);
}
