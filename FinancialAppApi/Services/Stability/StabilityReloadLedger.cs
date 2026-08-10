using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Stability;

public readonly record struct ReloadMovement(
    DateOnly Date,
    decimal Change,
    decimal Repayment,
    bool Marked);

public sealed record ReloadState(
    decimal Outstanding,
    DateOnly? OldestOutstandingDate,
    decimal MarkedThisRun,
    decimal RepaidThisRun);

/// <summary>
/// Replays the user's explicit emergency-fund reload choices. Balance movement and reload intent
/// are deliberately separate: an ordinary salary share is money into Stability, but it is not a
/// promise to replace a marked withdrawal. The opening queue is represented by its total and oldest
/// date because the cycle cache only needs to carry the obligation forward.
/// </summary>
public static class StabilityReloadLedger
{
    public static ReloadState Replay(
        ReloadState opening,
        decimal openingBalance,
        decimal target,
        IEnumerable<ReloadMovement> movements)
    {
        var queue = new LinkedList<(DateOnly Date, decimal Amount)>();
        var outstanding = Math.Max(0m, opening.Outstanding);
        if (outstanding > 0m && opening.OldestOutstandingDate.HasValue)
        {
            queue.AddLast((opening.OldestOutstandingDate.Value, outstanding));
        }

        var running = openingBalance;
        var markedThisRun = 0m;
        var repaidThisRun = 0m;

        void ClearAtTarget()
        {
            queue.Clear();
            outstanding = 0m;
            repaidThisRun = 0m;
        }

        // A setting change can lower the target without adding a movement to this cycle. The
        // opening balance must therefore discharge an inherited obligation before replay starts.
        if (target > 0m && running >= target)
        {
            ClearAtTarget();
        }

        foreach (var movement in movements)
        {
            running += movement.Change;

            if (movement.Marked && movement.Change < 0m)
            {
                var marked = -movement.Change;
                markedThisRun += marked;
                outstanding += marked;
                queue.AddLast((movement.Date, marked));
            }

            var repayment = Math.Min(outstanding, Math.Max(0m, movement.Repayment));
            if (repayment > 0m)
            {
                var remaining = repayment;
                while (remaining > 0m && queue.Count > 0)
                {
                    var node = queue.First!;
                    var oldest = node.Value;
                    queue.RemoveFirst();
                    var discharged = Math.Min(oldest.Amount, remaining);
                    remaining -= discharged;
                    var left = oldest.Amount - discharged;
                    if (left > 0m)
                    {
                        queue.AddFirst((oldest.Date, left));
                    }
                }

                outstanding -= repayment;
                repaidThisRun += repayment;
            }

            if (target > 0m && running >= target)
            {
                ClearAtTarget();
            }
        }

        return new ReloadState(
            Math.Max(0m, outstanding),
            queue.First?.Value.Date,
            markedThisRun,
            repaidThisRun);
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

    /// <summary>
    /// Describes a cycle's logical movements, omitting generated income children so an ordinary
    /// salary is not replayed twice. This is the only caller-facing grouping rule.
    /// </summary>
    public static IReadOnlyList<ReloadMovement> DescribeAll(
        IEnumerable<Transaction> transactions,
        decimal stabilityAlloc)
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
                movement = DescribeIncomeSplit(transaction, stabilityAlloc, change);
            }
            else
            {
                movement = Describe(transaction, stabilityAlloc);
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
        var repayment = change > 0m
            ? storedRepayment.HasValue
                ? Math.Max(0m, storedRepayment.Value)
                : Math.Max(0m, change - normalSalaryShare)
            : 0m;
        var marked = change < 0m
            && !string.Equals(
                StabilityReloadIntent.Normalize(transaction.StabilityReloadIntent),
                StabilityReloadIntent.NotRequired,
                StringComparison.Ordinal);
        return new ReloadMovement(TransactionDate.ToDateOnly(transaction.Date), change, repayment, marked);
    }

    private static bool IsIncome(Transaction transaction) =>
        string.Equals(transaction.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase)
        || transaction.LedgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase);
}
