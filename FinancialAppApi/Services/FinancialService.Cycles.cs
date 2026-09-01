using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class FinancialService
{
    // Shared by GetDashboardDataAsync and GetDashboardInsightsAsync: loads (or creates) the
    // settings row and resolves which cycle is "active" -- either the caller's explicit
    // month/year, or today's detected cycle when omitted. `persist` controls whether the
    // resolution is written back as the new SelectedMonth/SelectedYear: the dashboard endpoint
    // stays the writer of record (persist: true, preserving its existing behavior exactly),
    // while insights is a pure reader (persist: false) so the two endpoints -- fetched in
    // parallel by the frontend with the same explicit month/year -- don't both redundantly
    // re-save the identical values.
    /// <summary>
    /// Resolves the cycle the client should be shown, for callers that need the period up
    /// front rather than as a side effect of building a payload.
    /// </summary>
    /// <remarks>
    /// The bootstrap endpoint uses this: when the client sends no month/year, the active
    /// period comes from today's date and the cycle day, which the frontend previously had
    /// to learn by awaiting the dashboard response before it could request transactions and
    /// insights. Resolving once here lets the whole boot payload be built for one known
    /// period with no waterfall.
    /// </remarks>
    public async Task<(string month, int year)> ResolveActivePeriodAsync(
        string? queryMonth,
        int? queryYear,
        bool persist,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveCycleContextAsync(
            queryMonth,
            queryYear,
            persist,
            cancellationToken);
        return (context.ActiveMonth, context.ActiveYear);
    }

    private async Task<FinancialCycleContext> ResolveCycleContextAsync(
        string? queryMonth,
        int? queryYear,
        bool persist,
        CancellationToken cancellationToken)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        if (setting == null)
        {
            var now = _financialClock.LocalNow;
            setting = new FinancialSetting
            {
                TargetStabilityFund = 10000.00m,
                SelectedMonth = FinancialConstants.MonthAbbreviations[now.Month - 1],
                SelectedYear = now.Year,
                EssentialsAlloc = 0.50m,
                GrowthAlloc = 0.25m,
                StabilityAlloc = 0.15m,
                RewardsAlloc = 0.10m,
                CycleDay = 28,
                HideSensitive = false
            };
            _context.FinancialSettings.Add(setting);
            await _context.SaveChangesAsync(cancellationToken);
            await _stabilityPlanRevisionService.EnsureBaselineAsync(setting, cancellationToken);
        }

        var cycleDay = setting.CycleDay;

        string activeMonth;
        int activeYear;

        if (string.IsNullOrEmpty(queryMonth) || queryYear == null)
        {
            var detected = CategoryAttributionService.GetCycleMonthAndYearForDate(_financialClock.LocalNow, cycleDay);
            activeMonth = detected.month;
            activeYear = detected.year;
        }
        else
        {
            activeMonth = queryMonth;
            activeYear = queryYear.Value;
        }

        if (persist && (setting.SelectedMonth != activeMonth || setting.SelectedYear != activeYear))
        {
            setting.SelectedMonth = activeMonth;
            setting.SelectedYear = activeYear;
            await _context.SaveChangesAsync(cancellationToken);
        }

        var activeMonthIndex = Array.IndexOf(Months, activeMonth) + 1;
        if (activeMonthIndex == 0) throw new ArgumentException("Invalid month.", nameof(queryMonth));

        return new FinancialCycleContext(
            setting,
            cycleDay,
            activeMonth,
            activeYear,
            activeMonthIndex);
    }

    private async Task<List<Transaction>> GetTransactionsForCycleAsync(
        int year,
        int monthIndex,
        int cycleDay,
        CancellationToken cancellationToken)
    {
        var (start, end, _) = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);
        var startDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(start));
        var endExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(end));
        return await _context.Transactions
            .AsNoTracking()
            .Where(t => t.Date >= startDate && t.Date < endExclusive)
            .ToListAsync(cancellationToken);
    }

    private static List<Transaction> GetActiveCycleTransactions(
        FinancialBootstrapSnapshot snapshot)
    {
        var activeRange = CategoryAttributionService.GetCycleRange(
            snapshot.Cycle.ActiveYear,
            snapshot.Cycle.ActiveMonthIndex,
            snapshot.Cycle.CycleDay);
        var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(activeRange.start));
        var endExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(activeRange.end));
        return snapshot.CycleRelevantTransactions
            .Where(transaction => transaction.Date >= start && transaction.Date < endExclusive)
            .ToList();
    }

    private static List<ActiveRecurringItem> BuildActiveRecurringList(
        List<RecurringPaymentOccurrence> occurrences,
        IReadOnlyCollection<Transaction>? transactions = null)
    {
        var nonDiscardedTxs = transactions?
            .Where(t => t.RecurringPaymentId != null && t.RecurringOccurrenceDate != null && !string.Equals(t.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase))
            .GroupBy(t => (PaymentId: t.RecurringPaymentId!, Date: t.RecurringOccurrenceDate!.Value))
            .ToDictionary(g => g.Key, g => g.Sum(t => Math.Abs(t.Amount))) ?? new();

        return occurrences.Select(occurrence =>
        {
            // An occurrence with no scheduled amount is genuinely unknown. Sending 0 for the
            // scheduled/remaining pair made every client `remaining ?? scheduled ?? amount`
            // fallback stop at a fabricated zero instead of reaching the honest "not set" state.
            decimal? scheduled = occurrence.ScheduledAmount.HasValue
                ? Math.Abs(occurrence.ScheduledAmount.Value)
                : null;
            var paid = nonDiscardedTxs.GetValueOrDefault((occurrence.RecurringPaymentId, occurrence.OccurrenceDate));
            decimal? remaining = scheduled.HasValue ? Math.Max(0m, scheduled.Value - paid) : null;

            return new ActiveRecurringItem(
                occurrence.Id,
                occurrence.RecurringPaymentId,
                occurrence.Name,
                scheduled.HasValue
                    ? ObfuscationHelper.Obfuscate(occurrence.Status == RecurringOccurrenceStatus.PartiallyPaid ? remaining!.Value : scheduled.Value)
                    : null,
                occurrence.Category ?? string.Empty,
                occurrence.LedgerCategory ?? string.Empty,
                occurrence.OccurrenceDate.ToString("yyyy-MM-dd"),
                occurrence.Status == RecurringOccurrenceStatus.Paid,
                occurrence.Status == RecurringOccurrenceStatus.Discarded,
                occurrence.Status,
                occurrence.PaidDate?.ToString("yyyy-MM-dd"),
                scheduledAmount: scheduled.HasValue ? ObfuscationHelper.Obfuscate(scheduled.Value) : null,
                paidAmount: ObfuscationHelper.Obfuscate(paid),
                remainingAmount: remaining.HasValue ? ObfuscationHelper.Obfuscate(remaining.Value) : null);
        }).ToList();
    }

    // Walks cycle boundaries backward n-1 times (pure date math, no DB access) to find the start
    // of the cycle n positions back from (year, monthIndex) inclusive -- e.g. n=1 returns that
    // same cycle's own start, n=3 returns the start of the cycle 2 before it.
    private static DateTime GetCycleStartNCyclesBack(int year, int monthIndex, int cycleDay, int n)
    {
        int y = year, m = monthIndex;
        for (int i = 1; i < n; i++)
        {
            m--;
            if (m < 1) { m = 12; y--; }
        }
        return CategoryAttributionService.GetCycleRange(y, m, cycleDay).start;
    }

    // Same averaging semantics as the old per-cycle-query version, but buckets an already-fetched
    // transaction list (covering the same 3 cycles) instead of issuing 3 more queries for them.
    private static (decimal average, bool hasHistory) CalculatePastRewardsAverageFromTxs(
        List<Transaction> last3Txs, int activeYear, int activeMonthIndex, int cycleDay)
    {
        decimal totalPastRewards = 0;
        int activeMonthsCount = 0;
        int tempMonth = activeMonthIndex;
        int tempYear = activeYear;

        tempMonth--;
        if (tempMonth < 1)
        {
            tempMonth = 12;
            tempYear--;
        }

        for (int i = 0; i < 3; i++)
        {
            var (cycleStart, cycleEnd, _) = CategoryAttributionService.GetCycleRange(tempYear, tempMonth, cycleDay);
            var cycleStartDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(cycleStart));
            var cycleEndExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(cycleEnd));
            var cycleTxsForMonth = last3Txs.Where(t => t.Date >= cycleStartDate && t.Date < cycleEndExclusive).ToList();

            if (cycleTxsForMonth.Count > 0)
            {
                var positiveRewards = cycleTxsForMonth
                    .Select(t => CategoryAttributionService.GetCategoryAmount(t, "Rewards"))
                    .Where(amt => amt > 0)
                    .Sum();

                totalPastRewards += positiveRewards;
                activeMonthsCount++;
            }

            tempMonth--;
            if (tempMonth < 1)
            {
                tempMonth = 12;
                tempYear--;
            }
        }

        if (activeMonthsCount == 0)
        {
            return (0m, false);
        }

        var average = totalPastRewards / activeMonthsCount;
        return (average, average > 0);
    }

    private async Task<List<int>> GetAvailableYearsAsync(CancellationToken cancellationToken)
    {
        var yearRange = await _context.Transactions
            .GroupBy(_ => 1)
            .Select(g => new { Min = g.Min(t => t.Date.Year), Max = g.Max(t => t.Date.Year) })
            .FirstOrDefaultAsync(cancellationToken);

        var currentYear = _financialClock.LocalNow.Year;
        var minYear = yearRange?.Min ?? 0;
        var maxYear = yearRange?.Max ?? 0;

        if (minYear == 0 || maxYear == 0)
        {
            return [currentYear];
        }

        minYear = Math.Min(minYear, currentYear);
        maxYear = Math.Max(maxYear, currentYear);

        return Enumerable.Range(minYear, maxYear - minYear + 1).ToList();
    }

    private static bool IsActiveInRange(RecurringPayment payment, DateTime rangeStart, DateTime rangeEnd)
    {
        if (!payment.Active ||
            !DateOnly.TryParseExact(payment.StartDate, "yyyy-MM-dd", out var startDate))
        {
            return false;
        }

        if (startDate > DateOnly.FromDateTime(rangeEnd)) return false;
        if (string.IsNullOrWhiteSpace(payment.EndDate)) return true;
        return DateOnly.TryParseExact(payment.EndDate, "yyyy-MM-dd", out var endDate) &&
            endDate >= DateOnly.FromDateTime(rangeStart);
    }

    private static decimal MonthlyEquivalent(RecurringPayment payment) =>
        string.Equals(payment.Frequency, "Annually", StringComparison.OrdinalIgnoreCase)
            ? payment.Amount / 12m
            : payment.Amount;

    private static ReportTrendPoint ToTrendPoint(CycleBalance balance) => new(
        $"{balance.Year:D4}-{balance.MonthIndex:D2}",
        Months[balance.MonthIndex - 1],
        balance.GrowthBalance);

    private static List<object> ObfuscateTrendPoints(IEnumerable<ReportTrendPoint> points) =>
        points.Select(point => (object)new
        {
            cycleKey = point.CycleKey,
            month = point.Month,
            balance = ObfuscationHelper.Obfuscate(point.Balance)
        }).ToList();

    private static List<object> ObfuscateBreakdown(IEnumerable<ReportBreakdownItem> breakdown) =>
        breakdown.Select(item => (object)new
        {
            category = item.Category,
            amount = ObfuscationHelper.Obfuscate(item.Amount)
        }).ToList();
}
