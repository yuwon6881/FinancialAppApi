using System.Globalization;
using FinancialAppApi.Database;

namespace FinancialAppApi.Services;

// Phase 6: server-side wishlist forecasting. The math is deliberately deterministic and
// lives here (not in the model) so a "how long until I can afford X" answer is numerically
// verifiable. Key correctness rules over the old implementation:
//   * the per-cycle "savings" median includes negative and zero cycles -- discarding bad
//     cycles before taking the median systematically overstates how fast the user saves;
//   * the target date is anchored to the START OF THE CURRENT ACTIVE CYCLE, not cycles[^1]
//     (whose ordering was never guaranteed);
//   * a wishlist reference that matches more than one active item returns a clarification
//     marker instead of silently forecasting every item.
public partial class AiAssistantService
{
    internal enum WishlistForecastStatus
    {
        Estimated,
        AlreadyReached,
        NotReachable,
        InsufficientData,
        MultipleMatches
    }

    // Transfers are always excluded from savings. AvailableFunds is an explicit policy input
    // -- until the app has a dedicated wishlist-savings balance, it stays 0 so "remaining"
    // equals the full price rather than borrowing an unrelated ledger balance.
    internal sealed record WishlistForecastPolicy(
        IReadOnlyList<AiWishlistRow> Wishlist,
        IReadOnlyList<AiTransactionRow> Transactions,
        IReadOnlyList<CycleKey> Cycles,
        int CycleDay,
        DateTime ActiveCycleStart,
        string? WishlistReference,
        decimal AvailableFunds = 0m);

    internal sealed record WishlistForecastResult(
        int WishlistItemId,
        string Name,
        decimal Price,
        decimal AvailableFunds,
        decimal RemainingAmount,
        decimal? TypicalSavingsPerCycle,
        int? EstimatedCycles,
        string? EstimatedDate,
        WishlistForecastStatus Status,
        IReadOnlyList<decimal> CycleSavings,
        string Assumption,
        IReadOnlyList<string>? Candidates = null);

    private static List<object>? BuildWishlistForecast(WishlistForecastPolicy policy)
    {
        var results = ComputeWishlistForecast(policy);
        if (results.Count == 0) return null;
        return results.Select(r => (object)new
        {
            id = r.WishlistItemId,
            name = r.Name,
            remaining = r.RemainingAmount,
            availableFunds = r.AvailableFunds,
            estimatedCycles = r.EstimatedCycles,
            estimatedTargetDate = r.EstimatedDate,
            savingsPerCycle = r.TypicalSavingsPerCycle,
            cycleSavings = r.CycleSavings,
            assumptions = r.Assumption,
            status = ToStatusString(r.Status),
            candidates = r.Candidates
        }).ToList();
    }

    private static string ToStatusString(WishlistForecastStatus status) => status switch
    {
        WishlistForecastStatus.Estimated => "estimated-from-completed-cycles",
        WishlistForecastStatus.AlreadyReached => "already-reached",
        WishlistForecastStatus.NotReachable => "not-currently-reachable",
        WishlistForecastStatus.InsufficientData => "insufficient-cycle-data",
        WishlistForecastStatus.MultipleMatches => "multiple-matches",
        _ => "unknown"
    };

    internal static IReadOnlyList<WishlistForecastResult> ComputeWishlistForecast(WishlistForecastPolicy policy)
    {
        var candidates = policy.Wishlist.Where(w => w.IsActive && !w.IsPurchased).ToList();
        if (candidates.Count == 0) return [];

        // Resolve which item(s) the user meant. A concrete reference wins; ambiguity is
        // surfaced as a clarification rather than forecasting the whole list.
        if (!string.IsNullOrWhiteSpace(policy.WishlistReference))
        {
            var reference = policy.WishlistReference.Trim();
            var matched = candidates
                .Where(w => w.Name.Contains(reference, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matched.Count > 1)
            {
                return
                [
                    new WishlistForecastResult(
                        matched[0].Id, reference, 0m, policy.AvailableFunds, 0m, null, null, null,
                        WishlistForecastStatus.MultipleMatches, [],
                        $"\"{reference}\" matches {matched.Count} active wishlist items; ask which one.",
                        matched.Select(w => w.Name).ToList())
                ];
            }
            if (matched.Count == 1) candidates = matched;
        }

        // Per-cycle net savings across the requested COMPLETED cycles, including negative and
        // zero cycles. Sorted only to take the median.
        var cycleSavings = policy.Cycles
            .Select(cycle => CycleNetSavings(policy.Transactions, cycle, policy.CycleDay))
            .ToList();
        if (cycleSavings.Count == 0)
        {
            return candidates.Select(w => new WishlistForecastResult(
                w.Id, w.Name, w.Price, policy.AvailableFunds, Math.Max(0m, w.Price - policy.AvailableFunds),
                null, null, null, WishlistForecastStatus.InsufficientData, [],
                "No completed cycles were available to estimate savings.")).ToList();
        }

        var median = Median(cycleSavings);
        var sortedForDisplay = cycleSavings.OrderBy(x => x).ToList();

        return candidates.Select(w =>
        {
            var remaining = Math.Max(0m, w.Price - policy.AvailableFunds);
            if (remaining <= 0m)
            {
                return new WishlistForecastResult(
                    w.Id, w.Name, w.Price, policy.AvailableFunds, 0m, median, 0, null,
                    WishlistForecastStatus.AlreadyReached, sortedForDisplay,
                    "Available funds already cover this item.");
            }
            if (median <= 0m)
            {
                return new WishlistForecastResult(
                    w.Id, w.Name, w.Price, policy.AvailableFunds, remaining, median, null, null,
                    WishlistForecastStatus.NotReachable, sortedForDisplay,
                    $"Median net savings across {cycleSavings.Count} completed cycles is {median} (<= 0); not currently on track. Transfers excluded.");
            }

            var estimatedCycles = Math.Max(1, (int)Math.Ceiling(remaining / median));
            var targetDate = policy.ActiveCycleStart
                .AddMonths(estimatedCycles)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return new WishlistForecastResult(
                w.Id, w.Name, w.Price, policy.AvailableFunds, remaining, median, estimatedCycles, targetDate,
                WishlistForecastStatus.Estimated, sortedForDisplay,
                $"Median net savings across {cycleSavings.Count} completed cycles (incl. negative/zero); transfers excluded; anchored to the current cycle start.");
        }).ToList();
    }

    private static decimal CycleNetSavings(IReadOnlyList<AiTransactionRow> transactions, CycleKey cycle, int cycleDay)
    {
        var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
        var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start));
        var end = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end));
        return transactions
            .Where(t => t.Timestamp >= start && t.Timestamp < end && !IsTransfer(t))
            .Sum(t => t.Amount);
    }

    private static decimal Median(IReadOnlyList<decimal> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        var count = sorted.Count;
        if (count == 0) return 0m;
        return count % 2 == 1
            ? sorted[count / 2]
            : (sorted[(count / 2) - 1] + sorted[count / 2]) / 2m;
    }
}
