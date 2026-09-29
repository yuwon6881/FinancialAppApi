using System.Globalization;
using FinancialAppApi.Database;

namespace FinancialAppApi.Services;

// Pure projections the forecast and recurring tools quote verbatim, so the model never does the
// arithmetic itself: a ledger bucket's time-to-target and the normalized recurring cost.
public partial class AiAssistantService
{
    internal sealed record LedgerBalanceForecastResult(
        string LedgerCategory,
        decimal Target,
        decimal CurrentBalance,
        decimal Remaining,
        decimal? SavingsPerCycle,
        int? EstimatedCycles,
        string? EstimatedDate,
        string Status,
        IReadOnlyList<decimal> CycleSavings,
        string Assumption);

    // Same projection the Wishlist page/forecast uses, generalized to an arbitrary ledger: the
    // savings rate is the average POSITIVE per-cycle attribution to that ledger across cycles that
    // had activity (empty cycles skipped, never averaged in as zero); the target date is today +
    // ceil(months * 30) days.
    internal static LedgerBalanceForecastResult ComputeLedgerBalanceForecast(
        string ledgerCategory,
        decimal target,
        decimal currentBalance,
        IReadOnlyList<AiTransactionRow> transactions,
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        DateTime today)
    {
        var remaining = Math.Max(0m, target - currentBalance);
        var perCycle = cycles
            .Select(cycle => CyclePositiveLedger(transactions, cycle, cycleDay, ledgerCategory))
            .Where(v => v.HasActivity)
            .Select(v => v.Amount)
            .ToList();

        if (remaining <= 0m)
        {
            return new LedgerBalanceForecastResult(
                ledgerCategory, target, currentBalance, 0m, perCycle.Count > 0 ? perCycle.Sum() / perCycle.Count : null,
                0, null, "already-reached", perCycle,
                $"Your current {ledgerCategory} balance already meets this target.");
        }
        if (perCycle.Count == 0)
        {
            return new LedgerBalanceForecastResult(
                ledgerCategory, target, currentBalance, remaining, null, null, null,
                "insufficient-cycle-data", perCycle,
                $"No cycles with activity were available to estimate a {ledgerCategory} savings rate.");
        }

        var rate = perCycle.Sum() / perCycle.Count;
        if (rate <= 0m)
        {
            return new LedgerBalanceForecastResult(
                ledgerCategory, target, currentBalance, remaining, rate, null, null,
                "not-currently-reachable", perCycle,
                $"Average {ledgerCategory} saved across {perCycle.Count} active cycle(s) is {rate} (<= 0); not currently on track.");
        }

        var months = remaining / rate;
        var estimatedCycles = Math.Max(1, (int)Math.Ceiling(months));
        var days = (int)Math.Ceiling(months * 30m);
        var targetDate = today.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new LedgerBalanceForecastResult(
            ledgerCategory, target, currentBalance, remaining, rate, estimatedCycles, targetDate,
            "estimated-from-completed-cycles", perCycle,
            $"Average positive {ledgerCategory} saved across {perCycle.Count} active cycle(s); remaining = target minus current {ledgerCategory} balance; projected as today + {days} days.");
    }

    private static (decimal Amount, bool HasActivity) CyclePositiveLedger(
        IReadOnlyList<AiTransactionRow> transactions, CycleKey cycle, int cycleDay, string ledgerCategory)
    {
        var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
        var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start));
        var end = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end));
        var inCycle = transactions.Where(t => t.Timestamp >= start && t.Timestamp < end).ToList();
        if (inCycle.Count == 0) return (0m, false);
        var positive = inCycle
            .Select(t => CategoryAttributionService.GetCategoryAmount(new Models.Transaction
            {
                Amount = t.Amount,
                LedgerCategory = t.LedgerCategory
            }, ledgerCategory))
            .Where(amount => amount > 0)
            .Sum();
        return (positive, true);
    }

    private static decimal MonthlyEquivalent(decimal amount, string frequency) => frequency.ToLowerInvariant() switch
    {
        "weekly" => amount * 52m / 12m,
        "annually" => amount / 12m,
        _ => amount // Monthly (and any unknown cadence) counts once per month
    };

    // Active recurring charges normalized to a common monthly basis and summed, plus the annual
    // figure and a per-ledger monthly breakdown, so cadence differences (weekly vs annually) are
    // reconciled by the server instead of left to the model to get right.
    internal static object BuildRecurringCostSummary(IReadOnlyList<AiRecurringRow> recurring)
    {
        var active = recurring.Where(r => r.Active).ToList();
        var monthlyTotal = active.Sum(r => MonthlyEquivalent(Math.Abs(r.Amount), r.Frequency));
        var perLedger = active
            .GroupBy(r => r.LedgerCategory)
            .Select(g => new
            {
                ledgerCategory = g.Key,
                monthly = Math.Round(g.Sum(r => MonthlyEquivalent(Math.Abs(r.Amount), r.Frequency)), 2)
            })
            .OrderByDescending(x => x.monthly)
            .ToList();
        return new
        {
            note = "Active recurring charges normalized to a monthly basis (weekly x52/12, annually /12).",
            activeCount = active.Count,
            monthlyTotal = Math.Round(monthlyTotal, 2),
            annualTotal = Math.Round(monthlyTotal * 12m, 2),
            perLedgerMonthly = perLedger
        };
    }
}
