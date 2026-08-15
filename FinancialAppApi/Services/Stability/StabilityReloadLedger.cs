using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Stability;

public readonly record struct ReloadMovement(
    DateOnly Date,
    decimal Change,
    decimal Repayment,
    bool Marked,
    string? TransactionId = null,
    DateTime? Timestamp = null);

public sealed record ReloadObligation(
    string TransactionId,
    decimal OriginalAmount,
    decimal RemainingAmount);

public sealed record ReloadPlanPoint(DateTime EffectiveAt, decimal TargetStabilityFund);

public sealed record ReloadState(
    decimal Outstanding,
    DateOnly? OldestOutstandingDate,
    decimal MarkedThisRun,
    decimal RepaidThisRun,
    // The oldest drawdown marked during this run, still outstanding or not. The reporting window
    // needs it because FIFO repayment retires the oldest entries first, so a drawdown this cycle
    // paid off in full leaves the queue entirely -- and a window anchored only on what is still
    // outstanding then excludes it, understating both what was marked and what went back by the
    // same amount.
    DateOnly? OldestMarkedThisRunDate = null,
    // Detailed entries are returned when the replay started with transaction identities. The cycle
    // cache still carries only the aggregate queue, while query/bootstrap status uses these entries
    // to distinguish outstanding, partial and complete rows.
    IReadOnlyList<ReloadObligation>? Obligations = null);

/// <summary>
/// Replays the user's explicit emergency-fund reload choices. Balance movement and reload intent
/// are deliberately separate: an ordinary salary share is money into Stability, but it is not a
/// promise to replace a marked withdrawal. Plan points are processed chronologically, so a later
/// target change cannot reopen an obligation cleared under the target that was previously active.
/// </summary>
public static class StabilityReloadLedger
{
    public static ReloadState Replay(
        ReloadState opening,
        decimal openingBalance,
        decimal target,
        IEnumerable<ReloadMovement> movements) =>
        Replay(
            opening,
            openingBalance,
            [new ReloadPlanPoint(DateTime.MinValue, target)],
            movements);

    public static ReloadState Replay(
        ReloadState opening,
        decimal openingBalance,
        IEnumerable<ReloadPlanPoint> planPoints,
        IEnumerable<ReloadMovement> movements)
    {
        // The obligation is the queue and nothing else. A carried obligation may have no date or
        // source id, but its amount must still be discharged. Detailed identities are retained when
        // available so the Ledger can show per-withdrawal completion without changing the cache's
        // compact aggregate shape.
        var queue = new LinkedList<ReloadQueueEntry>();
        var obligations = new Dictionary<string, ReloadObligation>(StringComparer.Ordinal);
        if (opening.Obligations is { Count: > 0 })
        {
            foreach (var obligation in opening.Obligations.Where(item => item.RemainingAmount > 0m))
            {
                queue.AddLast(new ReloadQueueEntry(
                    obligation.TransactionId,
                    null,
                    obligation.RemainingAmount));
                obligations[obligation.TransactionId] = obligation;
            }
        }
        else if (opening.Outstanding > 0m)
        {
            queue.AddLast(new ReloadQueueEntry(null, opening.OldestOutstandingDate, opening.Outstanding));
        }

        decimal Outstanding() => queue.Sum(entry => entry.Amount);

        var running = openingBalance;
        var markedThisRun = 0m;
        var repaidThisRun = 0m;
        DateOnly? oldestMarkedThisRun = null;
        var points = planPoints
            .OrderBy(point => Normalize(point.EffectiveAt))
            .ToList();
        var pointIndex = 0;
        var target = points.Count > 0 ? points[0].TargetStabilityFund : 0m;

        void ClearAtTarget()
        {
            foreach (var entry in queue)
            {
                if (entry.TransactionId is not null && obligations.TryGetValue(entry.TransactionId, out var obligation))
                {
                    obligations[entry.TransactionId] = obligation with { RemainingAmount = 0m };
                }
            }
            queue.Clear();
            repaidThisRun = 0m;
            // Attainment starts a fresh reporting window. MarkedThisRun intentionally remains an
            // audit total; OldestMarkedThisRunDate is what scopes the visible recovery window.
            oldestMarkedThisRun = null;
        }

        void ApplyPlanPoint(ReloadPlanPoint point)
        {
            target = point.TargetStabilityFund;
            if (target > 0m && running >= target) ClearAtTarget();
        }

        void ApplyPlanPointsThrough(DateTime timestamp)
        {
            var normalized = Normalize(timestamp);
            while (pointIndex < points.Count && Normalize(points[pointIndex].EffectiveAt) <= normalized)
            {
                ApplyPlanPoint(points[pointIndex]);
                pointIndex++;
            }
        }

        void RecordMarked(ReloadMovement movement)
        {
            var marked = -movement.Change;
            markedThisRun += marked;
            queue.AddLast(new ReloadQueueEntry(movement.TransactionId, movement.Date, marked));
            if (movement.TransactionId is not null)
            {
                obligations[movement.TransactionId] = new ReloadObligation(
                    movement.TransactionId,
                    marked,
                    marked);
            }
            if (!oldestMarkedThisRun.HasValue || movement.Date < oldestMarkedThisRun.Value)
            {
                oldestMarkedThisRun = movement.Date;
            }
        }

        void ApplyRepayment(decimal requested)
        {
            var repayment = Math.Min(Outstanding(), Math.Max(0m, requested));
            if (repayment <= 0m) return;

            var remaining = repayment;
            while (remaining > 0m && queue.Count > 0)
            {
                var oldest = queue.First!.Value;
                queue.RemoveFirst();
                var discharged = Math.Min(oldest.Amount, remaining);
                remaining -= discharged;
                var left = oldest.Amount - discharged;
                if (oldest.TransactionId is not null && obligations.TryGetValue(oldest.TransactionId, out var obligation))
                {
                    obligations[oldest.TransactionId] = obligation with
                    {
                        RemainingAmount = Math.Max(0m, obligation.RemainingAmount - discharged)
                    };
                }
                if (left > 0m)
                {
                    queue.AddFirst(oldest with { Amount = left });
                }
            }

            repaidThisRun += repayment;
        }

        foreach (var movement in movements)
        {
            var timestamp = movement.Timestamp ?? movement.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            ApplyPlanPointsThrough(timestamp);
            running += movement.Change;

            if (movement.Marked && movement.Change < 0m) RecordMarked(movement);
            ApplyRepayment(movement.Repayment);

            if (target > 0m && running >= target) ClearAtTarget();
        }

        // A target change can clear a carried queue even when no transaction was recorded after it.
        while (pointIndex < points.Count)
        {
            ApplyPlanPoint(points[pointIndex]);
            pointIndex++;
        }

        return new ReloadState(
            Outstanding(),
            queue.First?.Value.Date,
            markedThisRun,
            repaidThisRun,
            oldestMarkedThisRun,
            obligations.Values.OrderBy(item => item.TransactionId, StringComparer.Ordinal).ToList());
    }

    /// <summary>Describes one raw transaction when it is already a logical Stability movement.</summary>
    public static ReloadMovement? Describe(Transaction transaction, decimal stabilityAlloc)
    {
        var change = CategoryAttributionService.GetCategoryAmount(transaction, "Stability");
        if (change == 0m) return null;

        return DescribeCore(
            transaction,
            change,
            IsIncome(transaction) ? Math.Max(0m, transaction.Amount * stabilityAlloc) : 0m,
            IsIncome(transaction) ? transaction.StabilityRecoveryTopUpAmount : null);
    }

    public static bool CanCarryReloadStatus(Transaction transaction) =>
        Describe(transaction, 0m) is { Change: < 0m };

    /// <summary>
    /// Describes the generated Stability child together with its income parent. The parent is the
    /// logical salary movement; the child is only how the persisted balance is materialized.
    /// </summary>
    public static ReloadMovement? DescribeIncomeSplit(
        Transaction parent,
        decimal stabilityAlloc,
        decimal stabilityChange)
    {
        if (stabilityChange == 0m) return null;
        return DescribeCore(
            parent,
            stabilityChange,
            Math.Max(0m, parent.Amount * stabilityAlloc),
            parent.StabilityRecoveryTopUpAmount);
    }

    public static IReadOnlyList<ReloadMovement> DescribeAll(
        IEnumerable<Transaction> transactions,
        decimal stabilityAlloc) =>
        DescribeAll(transactions, _ => stabilityAlloc);

    /// <summary>
    /// Describes a cycle's logical movements, omitting generated income children so an ordinary
    /// salary is not replayed twice. Allocation is resolved per parent transaction from the plan
    /// revision that was active when it was posted.
    /// </summary>
    public static IReadOnlyList<ReloadMovement> DescribeAll(
        IEnumerable<Transaction> transactions,
        Func<Transaction, decimal> stabilityAllocation)
    {
        var ordered = transactions
            .OrderBy(transaction => transaction.Date)
            .ThenBy(transaction => transaction.PostedAt)
            .ThenBy(transaction => transaction.Id, StringComparer.Ordinal)
            .ToList();
        var stabilityChildren = ordered
            .Where(transaction => transaction.Id.EndsWith("-split-Stability", StringComparison.Ordinal))
            .ToDictionary(transaction => transaction.Id[..^"-split-Stability".Length], StringComparer.Ordinal);
        var movements = new List<ReloadMovement>();

        foreach (var transaction in ordered)
        {
            if (transaction.Id.Contains("-split-", StringComparison.Ordinal)) continue;

            ReloadMovement? movement;
            if (IsIncome(transaction)
                && stabilityChildren.TryGetValue(transaction.Id, out var stabilityChild))
            {
                var change = CategoryAttributionService.GetCategoryAmount(stabilityChild, "Stability");
                movement = DescribeIncomeSplit(transaction, stabilityAllocation(transaction), change);
            }
            else
            {
                movement = Describe(transaction, stabilityAllocation(transaction));
            }

            if (movement.HasValue) movements.Add(movement.Value);
        }

        return movements;
    }

    private static ReloadMovement DescribeCore(
        Transaction transaction,
        decimal change,
        decimal normalSalaryShare,
        decimal? storedRepayment)
    {
        var repayment = !transaction.IsAccountBalanceAdjustment && change > 0m
            ? storedRepayment.HasValue
                ? Math.Max(0m, storedRepayment.Value)
                : Math.Max(0m, change - normalSalaryShare)
            : 0m;
        var marked = !transaction.IsAccountBalanceAdjustment && change < 0m
            && !string.Equals(
                StabilityReloadIntent.Normalize(transaction.StabilityReloadIntent),
                StabilityReloadIntent.NotRequired,
                StringComparison.Ordinal);
        return new ReloadMovement(
            TransactionDate.ToDateOnly(transaction.Date),
            change,
            repayment,
            marked,
            transaction.Id,
            Normalize(transaction.PostedAt));
    }

    private static bool IsIncome(Transaction transaction) =>
        string.Equals(transaction.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase)
        || transaction.LedgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase);

    private static DateTime Normalize(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private readonly record struct ReloadQueueEntry(
        string? TransactionId,
        DateOnly? Date,
        decimal Amount);
}
