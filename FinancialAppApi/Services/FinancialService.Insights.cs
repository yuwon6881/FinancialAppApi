using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class FinancialService
{
    // Historical aggregates (yearly/last-3/last-6 category breakdowns, past-rewards average) split
    // out of GetDashboardDataAsync: they used to cost ~24 sequential per-month DB queries, so they
    // ran serialized behind the cheap current-cycle data on every dashboard load even though most
    // of the UI only needs them for the secondary trend/insights widgets. Fetched by the frontend
    // in parallel with the (now much cheaper) dashboard call instead.
    public async Task<object> GetDashboardInsightsAsync(
        string? queryMonth = null,
        int? queryYear = null,
        CancellationToken cancellationToken = default)
    {
        var cycle = await ResolveCycleContextAsync(
            queryMonth,
            queryYear,
            persist: false,
            cancellationToken);
        return await GetDashboardInsightsAsync(cycle, cancellationToken);
    }

    internal async Task<object> GetDashboardInsightsAsync(
        FinancialCycleContext cycle,
        CancellationToken cancellationToken)
    {
        var cycleDay = cycle.CycleDay;
        var activeYear = cycle.ActiveYear;
        var activeMonthIndex = cycle.ActiveMonthIndex;

        var activeRange = CategoryAttributionService.GetCycleRange(activeYear, activeMonthIndex, cycleDay);
        var activeCycleEndExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(activeRange.end));

        // Yearly breakdown only ever aggregated by category across the whole range. Push the
        // GroupBy/Sum into SQL so a full year of expense rows collapses to one row per category
        // in the database, instead of materializing every yearly transaction just to group it in
        // memory. The projection mirrors BuildBreakdown's semantics exactly (exclude transfers,
        // sum outflows by category, abs, order by amount desc).
        var (currentCycleYear, currentCycleMonthIndex) = CategoryAttributionService
            .GetCycleYearAndMonthIndexForDate(_financialClock.Today, cycleDay);
        var yearEndMonth = activeYear < currentCycleYear
            ? 12
            : activeYear == currentCycleYear ? currentCycleMonthIndex : 0;
        var yearlyGroups = new List<ReportCategoryTotal>();
        if (yearEndMonth > 0)
        {
            var yearStartDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(CategoryAttributionService.GetCycleRange(activeYear, 1, cycleDay).start));
            var yearEndExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(CategoryAttributionService.GetCycleRange(activeYear, yearEndMonth, cycleDay).end));
            yearlyGroups = (await _context.Transactions
                .AsNoTracking()
                .Where(t => t.Date >= yearStartDate && t.Date < yearEndExclusive)
                .Where(t => t.Amount < 0)
                .Where(t => t.Category.ToLower() != "transfer")
                .Where(t => t.Category.ToLower() != "adjustment")
                .Where(t => !t.LedgerCategory.ToLower().StartsWith("transfer:"))
                .Where(t => t.LedgerCategory.ToLower() != "accountmove")
                .Where(t => t.LedgerCategory.ToLower() != "discarded")
                .GroupBy(t => t.Category)
                .Select(g => new ReportCategoryTotal(g.Key, g.Sum(t => t.Amount)))
                .ToListAsync(cancellationToken));
        }
        var yearlyCategoryBreakdown = yearlyGroups
            .GroupBy(g => string.IsNullOrWhiteSpace(g.Category) ? "Other" : g.Category.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new ReportBreakdownItem(g.Key, Math.Abs(g.Sum(x => x.Total))))
            .OrderByDescending(item => item.Amount)
            // Same tiebreak as BuildBreakdown, so equal totals do not order differently between
            // the yearly range and every other range on the same chart.
            .ThenBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Last-6 is one range query; last-3 is a strict subset of that same range, sliced in
        // memory instead of querying again.
        var last6StartDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(GetCycleStartNCyclesBack(activeYear, activeMonthIndex, cycleDay, 6)));
        var last3StartDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(GetCycleStartNCyclesBack(activeYear, activeMonthIndex, cycleDay, 3)));
        // Insights only ever read four columns off these rows (BuildBreakdown needs Category /
        // LedgerCategory / Amount, the rewards average needs Date as well), so fetch those rather
        // than whole entities -- six cycles of ledger rows otherwise carries every Description and
        // denormalized id across the wire on a request the outbox re-issues after every drain.
        var last6Rows = await _context.Transactions
            .AsNoTracking()
            .Where(t => t.Date >= last6StartDate && t.Date < activeCycleEndExclusive)
            .Select(t => new { t.Date, t.Amount, t.Category, t.LedgerCategory })
            .ToListAsync(cancellationToken);
        var last6Txs = last6Rows
            .Select(r => new Transaction
            {
                Date = r.Date,
                Amount = r.Amount,
                Category = r.Category,
                LedgerCategory = r.LedgerCategory
            })
            .ToList();
        var last6CategoryBreakdown = ReportMetricsCalculator.BuildBreakdown(last6Txs);

        var last3Txs = last6Txs.Where(t => t.Date >= last3StartDate).ToList();
        var last3CategoryBreakdown = ReportMetricsCalculator.BuildBreakdown(last3Txs);

        // Use the three completed cycles before the active cycle; the current partial cycle would
        // otherwise depress the savings-rate average early in the month.
        var (pastThreeMonthsRewardsAverage, hasRewardsHistory) =
            CalculatePastRewardsAverageFromTxs(last6Txs, activeYear, activeMonthIndex, cycleDay);
        var availableYears = await GetAvailableYearsAsync(cancellationToken);

        return new
        {
            last3CategoryBreakdown = ObfuscateBreakdown(last3CategoryBreakdown),
            last6CategoryBreakdown = ObfuscateBreakdown(last6CategoryBreakdown),
            yearlyCategoryBreakdown = ObfuscateBreakdown(yearlyCategoryBreakdown),
            pastThreeMonthsRewardsAverage = ObfuscationHelper.Obfuscate(pastThreeMonthsRewardsAverage),
            hasRewardsHistory,
            availableYears
        };
    }
}
