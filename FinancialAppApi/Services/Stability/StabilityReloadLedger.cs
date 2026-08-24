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
    decimal RemainingAmount,
    DateOnly? Date = null);

public sealed record ReloadPlanPoint(DateTime EffectiveAt, decimal TargetStabilityFund);

public sealed record ReloadState(
    decimal Outstanding,
    DateOnly? OldestOutstandingDate,
    decimal MarkedThisRun,
    decimal RepaidThisRun,
    // Detailed entries are returned when the replay started with transaction identities. Entries
    // are retained after they are fully discharged so per-row status can still say "complete";
    // only the two totals below drop them.
    IReadOnlyList<ReloadObligation>? Obligations = null,
    // What is still owed, counted per obligation rather than per movement. OpenMarkedTotal sums the
    // original amount of every obligation with money still owing and OpenRepaidTotal sums what has
    // gone back against those same obligations, so a drawdown put back in full leaves both figures
    // entirely. By construction OpenMarkedTotal - OpenRepaidTotal == Outstanding: summing movements
    // instead let a settled drawdown keep inflating the reported total for as long as anything else
    // was owed.
    decimal OpenMarkedTotal = 0m,
    decimal OpenRepaidTotal = 0m);

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
        var obligationOrder = new List<string>();
        if (opening.Obligations is { Count: > 0 })
        {
            foreach (var obligation in opening.Obligations.Where(item => item.RemainingAmount > 0m))
            {
                queue.AddLast(new ReloadQueueEntry(
                    obligation.TransactionId,
                    obligation.Date,
                    obligation.RemainingAmount));
                obligations[obligation.TransactionId] = obligation;
                obligationOrder.Add(obligation.TransactionId);
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
        var points = planPoints
            .OrderBy(point => Normalize(point.EffectiveAt))
            .ToList();

        var sortedMovements = movements
            .Select((m, index) => (Movement: m, Timestamp: Normalize(m.Timestamp ?? m.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)), Index: index))
            .OrderBy(x => x.Timestamp)
            .ThenBy(x => x.Index)
            .ToList();

        var pointIndex = 0;
        var activeTarget = points.Count > 0 ? points[0].TargetStabilityFund : 0m;

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
            // Attainment settles everything: nothing is owed, so nothing is reported as owed. Only
            // MarkedThisRun survives, as an audit total of what left the fund during this run.
            repaidThisRun = 0m;
        }

        void ApplyPlanPoint(ReloadPlanPoint point)
        {
            activeTarget = point.TargetStabilityFund;
            if (activeTarget > 0m && running >= activeTarget) ClearAtTarget();
        }

        void ApplyPlanPointsThrough(DateTime timestamp)
        {
            var normalized = Normalize(timestamp);
            while (pointIndex < points.Count)
            {
                var point = points[pointIndex];
                if (Normalize(point.EffectiveAt) > normalized) break;
                ApplyPlanPoint(point);
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
                if (!obligations.ContainsKey(movement.TransactionId))
                {
                    obligationOrder.Add(movement.TransactionId);
                }
                obligations[movement.TransactionId] = new ReloadObligation(
                    movement.TransactionId,
                    marked,
                    marked,
                    movement.Date);
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

        if (sortedMovements.Count == 0)
        {
            while (pointIndex < points.Count)
            {
                ApplyPlanPoint(points[pointIndex]);
                pointIndex++;
            }
        }
        else
        {
            foreach (var item in sortedMovements)
            {
                var movement = item.Movement;
                ApplyPlanPointsThrough(item.Timestamp);
                running += movement.Change;

                if (movement.Marked && movement.Change < 0m) RecordMarked(movement);
                ApplyRepayment(movement.Repayment);

                if (activeTarget > 0m && running >= activeTarget) ClearAtTarget();
            }

            while (pointIndex < points.Count)
            {
                ApplyPlanPoint(points[pointIndex]);
                pointIndex++;
            }
        }

        // Only obligations that still owe money are reported. An anonymous carried entry has no
        // original amount to compare against, so its remaining amount is all it can contribute --
        // that entry shrinks as it is discharged, which keeps the difference equal to Outstanding().
        var openMarkedTotal = 0m;
        var openRepaidTotal = 0m;
        foreach (var obligation in obligations.Values.Where(item => item.RemainingAmount > 0m))
        {
            // Taking the larger of the two as the original keeps the difference equal to what is
            // still owed even for a malformed carried entry claiming more remaining than original.
            var original = Math.Max(obligation.OriginalAmount, obligation.RemainingAmount);
            openMarkedTotal += original;
            openRepaidTotal += original - obligation.RemainingAmount;
        }
        foreach (var entry in queue.Where(item => item.TransactionId is null))
        {
            openMarkedTotal += entry.Amount;
        }

        // Preserve replay insertion/FIFO order. The cycle cache filters this list to open entries
        // verbatim; sorting by date/id swaps same-day withdrawals and makes the next cycle's
        // repayment complete the wrong Ledger row.
        var orderedObligations = obligationOrder
            .Select(transactionId => obligations[transactionId])
            .ToList();

        return new ReloadState(
            Outstanding(),
            queue.First?.Value.Date,
            markedThisRun,
            repaidThisRun,
            orderedObligations,
            openMarkedTotal,
            openRepaidTotal);
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
        !transaction.IsAccountBalanceAdjustment && Describe(transaction, 0m) is { Change: < 0m };

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
