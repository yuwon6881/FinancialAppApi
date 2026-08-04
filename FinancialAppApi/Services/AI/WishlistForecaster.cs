using System.Globalization;
using FinancialAppApi.Database;

namespace FinancialAppApi.Services;

// Phase 6: server-side wishlist forecasting. The math is deliberately deterministic and
// lives here (not in the model) so a "how long until I can afford X" answer is numerically
// verifiable AND identical to what the app's own Wishlist page shows the user -- an assistant
// that quotes a different timeline than the screen is a bug. It therefore mirrors
// WishlistView.getTimelineString + FinancialService.CalculatePastRewardsAverageFromTxs exactly:
//   * the savings rate is the AVERAGE POSITIVE Rewards-ledger attribution across the last
//     cycles that had any activity (empty cycles are skipped, not averaged in as zero);
//   * "remaining" is the item price minus the current free Rewards balance (AvailableFunds),
//     because wishlist goals are funded from unearmarked Rewards, not total net cash flow;
//   * the target date is today + ceil(months * 30) days, exactly like the page;
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

    // AvailableFunds is the current free Rewards balance (the pool's unassigned amount), matching
    // the Wishlist page. RequiredPerCycle is removed from future Rewards inflow before projecting
    // a wishlist date, so commitments cannot be spent twice. Today is the reference date for the
    // target date, and Cycles are the trailing cycles used to compute the gross savings rate.
    internal sealed record WishlistForecastPolicy(
        IReadOnlyList<AiWishlistRow> Wishlist,
        IReadOnlyList<AiTransactionRow> Transactions,
        IReadOnlyList<CycleKey> Cycles,
        int CycleDay,
        DateTime ActiveCycleStart,
        DateTime Today,
        string? WishlistReference,
        decimal AvailableFunds = 0m,
        decimal RequiredPerCycle = 0m,
        decimal? BudgetedRewardsPerCycle = null);

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

        // Positive Rewards-ledger attribution per cycle, but ONLY over cycles that actually had
        // transactions -- exactly like FinancialService.CalculatePastRewardsAverageFromTxs, which
        // divides total past Rewards by the count of active months (not the full window). A cycle
        // with no activity is skipped, never averaged in as a zero.
        var perCycleRewards = policy.Cycles
            .Select(cycle => CyclePositiveRewards(policy.Transactions, cycle, policy.CycleDay))
            .Where(v => v.HasActivity)
            .Select(v => v.Rewards)
            .ToList();
        var grossRate = perCycleRewards.Count > 0
            ? perCycleRewards.Sum() / perCycleRewards.Count
            : policy.BudgetedRewardsPerCycle;
        if (!grossRate.HasValue)
        {
            return candidates.Select(w => new WishlistForecastResult(
                w.Id, w.Name, w.Price, policy.AvailableFunds, Math.Max(0m, w.Price - policy.AvailableFunds),
                null, null, null, WishlistForecastStatus.InsufficientData, [],
                "No cycles with activity were available to estimate a Rewards savings rate.")).ToList();
        }

        var rate = Math.Max(0m, grossRate.Value - policy.RequiredPerCycle);
        var perCycleForDisplay = perCycleRewards.ToList();
        var rateAssumption = perCycleRewards.Count > 0
            ? $"Average positive Rewards saved across {perCycleRewards.Count} active cycle(s), less {policy.RequiredPerCycle:0.00} per cycle reserved for savings goals"
            : $"Budgeted Rewards per cycle, less {policy.RequiredPerCycle:0.00} per cycle reserved for savings goals";

        return candidates.Select(w =>
        {
            var remaining = Math.Max(0m, w.Price - policy.AvailableFunds);
            if (remaining <= 0m)
            {
                return new WishlistForecastResult(
                    w.Id, w.Name, w.Price, policy.AvailableFunds, 0m, rate, 0, null,
                    WishlistForecastStatus.AlreadyReached, perCycleForDisplay,
                    "Your current Rewards balance already covers this item.");
            }
            if (rate <= 0m)
            {
                return new WishlistForecastResult(
                    w.Id, w.Name, w.Price, policy.AvailableFunds, remaining, rate, null, null,
                    WishlistForecastStatus.NotReachable, perCycleForDisplay,
                    $"{rateAssumption} leaves no free Rewards to put toward this item; not currently on track.");
            }

            // Mirror the app: months = remaining / rate, target date = today + ceil(months*30)
            // days. estimatedCycles is the whole-cycle ceiling of the same ratio.
            var months = remaining / rate;
            var estimatedCycles = Math.Max(1, (int)Math.Ceiling(months));
            var days = (int)Math.Ceiling(months * 30m);
            var targetDate = policy.Today
                .AddDays(days)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return new WishlistForecastResult(
                w.Id, w.Name, w.Price, policy.AvailableFunds, remaining, rate, estimatedCycles, targetDate,
                WishlistForecastStatus.Estimated, perCycleForDisplay,
                $"{rateAssumption}; remaining = price minus free Rewards now; projected as today + {days} days, matching the Wishlist page.");
        }).ToList();
    }

    // Sum of positive Rewards-ledger attribution in a cycle, and whether the cycle had any
    // transactions at all (drives the "active months" divisor). GetCategoryAmount routes
    // IncomeSplit/Transfer rows into their Rewards share, so this counts every way Rewards is fed.
    internal static (decimal Rewards, bool HasActivity) CyclePositiveRewards(
        IReadOnlyList<AiTransactionRow> transactions, CycleKey cycle, int cycleDay)
    {
        var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
        var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start));
        var end = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end));
        var inCycle = transactions.Where(t => t.Timestamp >= start && t.Timestamp < end).ToList();
        if (inCycle.Count == 0) return (0m, false);
        var positiveRewards = inCycle
            .Select(t => CategoryAttributionService.GetCategoryAmount(new Models.Transaction
            {
                Amount = t.Amount,
                LedgerCategory = t.LedgerCategory
            }, "Rewards"))
            .Where(amount => amount > 0)
            .Sum();
        return (positiveRewards, true);
    }
}
