using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using FinancialAppApi.Diagnostics;

namespace FinancialAppApi.Services;

public sealed record FinancialSettingsUpdate(
    string TargetStabilityFund,
    decimal EssentialsAlloc,
    decimal GrowthAlloc,
    decimal StabilityAlloc,
    decimal RewardsAlloc,
    int CycleDay,
    bool? DarkMode,
    bool? HideSensitive,
    bool? VibrationEnabled,
    string Currency,
    string? StabilityOverflowRedirect);

public class FinancialService
{
    private static readonly string[] Months = FinancialConstants.MonthAbbreviations;

    private readonly AppDbContext _context;
    private readonly CycleBalanceService _cycleBalanceService;
    private readonly RecurringPaymentAlertService _recurringPaymentAlertService;
    private readonly RecurringOccurrenceService _recurringOccurrenceService;
    private readonly FinancialClock _financialClock;

    public FinancialService(
        AppDbContext context,
        CycleBalanceService cycleBalanceService,
        RecurringPaymentAlertService recurringPaymentAlertService,
        RecurringOccurrenceService recurringOccurrenceService,
        FinancialClock? financialClock = null)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService;
        _recurringPaymentAlertService = recurringPaymentAlertService;
        _recurringOccurrenceService = recurringOccurrenceService;
        _financialClock = financialClock ?? FinancialClock.Utc;
    }

    public async Task<object> GetWalletBalanceAsync()
    {
        var setting = await GetOrCreateSettingAsync();
        var cycleDay = setting.CycleDay;

        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(_financialClock.Today, cycleDay);
        var currentCycleTxs = await GetTransactionsForCycleAsync(year, monthIndex, cycleDay);

        var (budgetEssentials, _, budgetStability, budgetRewards) =
            await _cycleBalanceService.GetOpeningBalanceAsync(year, monthIndex, cycleDay);

        var netEssentials = currentCycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, "Essentials"));
        var netStability = currentCycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, "Stability"));
        var netRewards = currentCycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, "Rewards"));

        var totalBalance = (budgetEssentials + netEssentials) + (budgetStability + netStability) + (budgetRewards + netRewards);
        return new { totalBalance = ObfuscationHelper.Obfuscate(totalBalance) };
    }

    public async Task<object> GetDashboardDataAsync(string? queryMonth = null, int? queryYear = null)
    {
        var stopwatch = Stopwatch.StartNew();
        using var activity = Telemetry.ActivitySource.StartActivity("FinancialService.GetDashboardData");

        var (setting, cycleDay, _, activeYear, activeMonthIndex) =
            await ResolveCycleContextAsync(queryMonth, queryYear, persist: true);

        var allRecurring = await _context.RecurringPayments.AsNoTracking().ToListAsync();

        var year = activeYear;

        var activeRange = CategoryAttributionService.GetCycleRange(year, activeMonthIndex, cycleDay);
        var activeRangeStartDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(activeRange.start));
        var activeRangeEndExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(activeRange.end));
        var activeCycleTxs = await _context.Transactions
            .AsNoTracking()
            .Where(t => t.Date >= activeRangeStartDate && t.Date < activeRangeEndExclusive)
            .ToListAsync();

        string selectedCycleLabel = activeRange.label;

        var (selectedBudgetEssentials, selectedBudgetGrowth, selectedBudgetStability, selectedBudgetRewards) =
            await _cycleBalanceService.GetOpeningBalanceAsync(year, activeMonthIndex, cycleDay);

        var selectedNetEssentials = activeCycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, "Essentials"));
        var selectedNetGrowth = activeCycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, "Growth"));
        var selectedNetStability = activeCycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, "Stability"));
        var selectedNetRewards = activeCycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, "Rewards"));

        var selectedRemEssentials = selectedBudgetEssentials + selectedNetEssentials;
        var selectedRemGrowth = selectedBudgetGrowth + selectedNetGrowth;
        var selectedRemStability = selectedBudgetStability + selectedNetStability;
        var selectedRemRewards = selectedBudgetRewards + selectedNetRewards;

        await _cycleBalanceService.EnsureComputedThroughAsync(year, activeMonthIndex, cycleDay);
        var trendRows = await _context.CycleBalances
            .AsNoTracking()
            .Where(b => b.Year == activeYear && b.MonthIndex <= activeMonthIndex)
            .OrderBy(b => b.MonthIndex)
            .ToListAsync();
        var trendPoints = trendRows.Select(r => (Months[r.MonthIndex - 1], r.GrowthBalance)).ToList();

        var selectedCycleIncome = activeCycleTxs
            .Where(t => t.LedgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase) || string.Equals(t.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase))
            .Sum(t => t.Amount);

        var targetEssentials = selectedCycleIncome * setting.EssentialsAlloc;
        var targetGrowth = selectedCycleIncome * setting.GrowthAlloc;
        var targetStability = selectedCycleIncome * setting.StabilityAlloc;
        var targetRewards = selectedCycleIncome * setting.RewardsAlloc;

        var categories = new[]
        {
            new { name = "Essentials", allocation = setting.EssentialsAlloc, target = ObfuscationHelper.Obfuscate(targetEssentials), budget = ObfuscationHelper.Obfuscate(selectedBudgetEssentials), netChange = ObfuscationHelper.Obfuscate(selectedNetEssentials), remaining = ObfuscationHelper.Obfuscate(selectedRemEssentials) },
            new { name = "Growth", allocation = setting.GrowthAlloc, target = ObfuscationHelper.Obfuscate(targetGrowth), budget = ObfuscationHelper.Obfuscate(selectedBudgetGrowth), netChange = ObfuscationHelper.Obfuscate(selectedNetGrowth), remaining = ObfuscationHelper.Obfuscate(selectedRemGrowth) },
            new { name = "Stability", allocation = setting.StabilityAlloc, target = ObfuscationHelper.Obfuscate(targetStability), budget = ObfuscationHelper.Obfuscate(selectedBudgetStability), netChange = ObfuscationHelper.Obfuscate(selectedNetStability), remaining = ObfuscationHelper.Obfuscate(selectedRemStability) },
            new { name = "Rewards", allocation = setting.RewardsAlloc, target = ObfuscationHelper.Obfuscate(targetRewards), budget = ObfuscationHelper.Obfuscate(selectedBudgetRewards), netChange = ObfuscationHelper.Obfuscate(selectedNetRewards), remaining = ObfuscationHelper.Obfuscate(selectedRemRewards) }
        };

        var totalBalance = selectedRemEssentials + selectedRemStability + selectedRemRewards;
        var monthlyInflow = activeCycleTxs.Where(t => t.Amount > 0 && !IsTransfer(t)).Sum(t => t.Amount);
        var monthlyOutflow = Math.Abs(activeCycleTxs.Where(t => t.Amount < 0 && !IsTransfer(t)).Sum(t => t.Amount));
        var activeRecurringTotal = allRecurring
            .Where(payment => IsActiveInRange(payment, activeRange.start, activeRange.end))
            .Sum(payment => Math.Abs(MonthlyEquivalent(payment)));
        var growthPercentAchieved = selectedNetGrowth / (targetGrowth > 0 ? targetGrowth : 1m);
        var stabilityPercentReached = selectedRemStability / (setting.TargetStabilityFund > 0 ? setting.TargetStabilityFund : 1m);

        var activeRecurringList = BuildActiveRecurringList(allRecurring, activeCycleTxs, activeRange.start, activeRange.end, cycleDay, activeYear, activeMonthIndex);
        var selectedMonthRecurring = activeRecurringList
            .OrderBy(r => ((dynamic)r).status == "Pending" ? 0 : ((dynamic)r).status == "Paid" ? 1 : 2)
            .ThenBy(r => ((dynamic)r).dueDate)
            .ToList();

        var pendingNotifications = await _recurringPaymentAlertService.GetSubscriptionAlertsAsync();

        var monthlyCategoryBreakdown = BuildBreakdown(activeCycleTxs);

        var trendPointsList = trendPoints.ToList();
        var last6TrendPoints = trendPointsList.Count >= 6
            ? trendPointsList.GetRange(trendPointsList.Count - 6, 6)
            : trendPointsList.ToList();
        var last3TrendPoints = trendPointsList.Count >= 3
            ? trendPointsList.GetRange(trendPointsList.Count - 3, 3)
            : trendPointsList.ToList();

        var result = new
        {
            setting = new
            {
                targetStabilityFund = ObfuscationHelper.Obfuscate(setting.TargetStabilityFund),
                selectedMonth = setting.SelectedMonth,
                selectedYear = setting.SelectedYear,
                essentialsAlloc = setting.EssentialsAlloc,
                growthAlloc = setting.GrowthAlloc,
                stabilityAlloc = setting.StabilityAlloc,
                rewardsAlloc = setting.RewardsAlloc,
                cycleDay = setting.CycleDay,
                darkMode = setting.DarkMode,
                hideSensitive = setting.HideSensitive,
                currency = setting.Currency,
                stabilityOverflowRedirect = setting.StabilityOverflowRedirect
            },
            cycleLabel = selectedCycleLabel,
            categories,
            stats = new
            {
                totalBalance = ObfuscationHelper.Obfuscate(totalBalance),
                monthlyIncome = ObfuscationHelper.Obfuscate(selectedCycleIncome),
                monthlyInflow = ObfuscationHelper.Obfuscate(monthlyInflow),
                monthlyExpenses = ObfuscationHelper.Obfuscate(monthlyOutflow),
                activeRecurringTotal = ObfuscationHelper.Obfuscate(activeRecurringTotal),
                growthPercentAchieved = (double)Math.Max(0, growthPercentAchieved),
                stabilityPercentReached = (double)Math.Max(0, stabilityPercentReached)
            },
            activeRecurringPayments = selectedMonthRecurring,
            trendPoints = ObfuscateTrendPoints(trendPoints),
            last3TrendPoints = ObfuscateTrendPoints(last3TrendPoints),
            last6TrendPoints = ObfuscateTrendPoints(last6TrendPoints),
            pendingNotifications,
            monthlyCategoryBreakdown = ObfuscateBreakdown(monthlyCategoryBreakdown)
        };

        stopwatch.Stop();
        Telemetry.DashboardLoadDuration.Record(stopwatch.ElapsedMilliseconds);

        return result;
    }

    // Historical aggregates (yearly/last-3/last-6 category breakdowns, past-rewards average) split
    // out of GetDashboardDataAsync: they used to cost ~24 sequential per-month DB queries, so they
    // ran serialized behind the cheap current-cycle data on every dashboard load even though most
    // of the UI only needs them for the secondary trend/insights widgets. Fetched by the frontend
    // in parallel with the (now much cheaper) dashboard call instead.
    public async Task<object> GetDashboardInsightsAsync(string? queryMonth = null, int? queryYear = null)
    {
        var (_, cycleDay, _, activeYear, activeMonthIndex) =
            await ResolveCycleContextAsync(queryMonth, queryYear, persist: false);

        var activeRange = CategoryAttributionService.GetCycleRange(activeYear, activeMonthIndex, cycleDay);
        var activeCycleEndExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(activeRange.end));

        // Yearly breakdown only ever aggregated by category across the whole range -- one query
        // spanning all 12 months replaces what used to be 12 separate per-month queries.
        var yearStartDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(CategoryAttributionService.GetCycleRange(activeYear, 1, cycleDay).start));
        var yearEndExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(CategoryAttributionService.GetCycleRange(activeYear, 12, cycleDay).end));
        var yearlyTxs = await _context.Transactions
            .AsNoTracking()
            .Where(t => t.Date >= yearStartDate && t.Date < yearEndExclusive)
            .ToListAsync();
        var yearlyCategoryBreakdown = BuildBreakdown(yearlyTxs);

        // Last-6 is one range query; last-3 is a strict subset of that same range, sliced in
        // memory instead of querying again.
        var last6StartDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(GetCycleStartNCyclesBack(activeYear, activeMonthIndex, cycleDay, 6)));
        var last3StartDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(GetCycleStartNCyclesBack(activeYear, activeMonthIndex, cycleDay, 3)));
        var last6Txs = await _context.Transactions
            .AsNoTracking()
            .Where(t => t.Date >= last6StartDate && t.Date < activeCycleEndExclusive)
            .ToListAsync();
        var last6CategoryBreakdown = BuildBreakdown(last6Txs);

        var last3Txs = last6Txs.Where(t => t.Date >= last3StartDate).ToList();
        var last3CategoryBreakdown = BuildBreakdown(last3Txs);

        // Use the three completed cycles before the active cycle; the current partial cycle would
        // otherwise depress the savings-rate average early in the month.
        var (pastThreeMonthsRewardsAverage, hasRewardsHistory) =
            CalculatePastRewardsAverageFromTxs(last6Txs, activeYear, activeMonthIndex, cycleDay);
        var availableYears = await GetAvailableYearsAsync();

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

    public async Task<string?> UpdateSettingsAsync(FinancialSettingsUpdate update)
    {
        if (!ObfuscationHelper.TryDeobfuscate(update.TargetStabilityFund, out var targetStabilityFund) || targetStabilityFund < 0m)
        {
            return "Target stability fund is malformed.";
        }
        var allocations = new[] { update.EssentialsAlloc, update.GrowthAlloc, update.StabilityAlloc, update.RewardsAlloc };
        if (allocations.Any(value => value < 0m || value > 1m))
        {
            return "Each income allocation must be between 0% and 100%.";
        }
        if (Math.Abs(allocations.Sum() - 1m) > 0.0001m)
        {
            return "Income allocations must total exactly 100%.";
        }

        var setting = await GetOrCreateSettingAsync();

        setting.TargetStabilityFund = targetStabilityFund;
        setting.EssentialsAlloc = update.EssentialsAlloc;
        setting.GrowthAlloc = update.GrowthAlloc;
        setting.StabilityAlloc = update.StabilityAlloc;
        setting.RewardsAlloc = update.RewardsAlloc;
        var newCycleDay = Math.Clamp(update.CycleDay, 1, 31);
        var cycleDayChanged = newCycleDay != setting.CycleDay;
        setting.CycleDay = newCycleDay;
        if (update.DarkMode.HasValue)
        {
            setting.DarkMode = update.DarkMode.Value;
        }
        if (update.HideSensitive.HasValue)
        {
            setting.HideSensitive = update.HideSensitive.Value;
        }
        if (update.VibrationEnabled.HasValue)
        {
            setting.VibrationEnabled = update.VibrationEnabled.Value;
        }
        if (update.StabilityOverflowRedirect != null)
        {
            setting.StabilityOverflowRedirect = update.StabilityOverflowRedirect;
        }
        setting.Currency = update.Currency;

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync();
            if (cycleDayChanged)
            {
                await _cycleBalanceService.InvalidateAllAsync();
            }
            await _context.SaveChangesAsync();
            await dbTransaction.CommitAsync();
        });
        return null;
    }

    public async Task UpdateDarkModeAsync(bool darkMode)
    {
        var setting = await GetOrCreateSettingAsync();
        setting.DarkMode = darkMode;
        await _context.SaveChangesAsync();
    }

    public async Task UpdateHideSensitiveAsync(bool hideSensitive)
    {
        var setting = await GetOrCreateSettingAsync();
        setting.HideSensitive = hideSensitive;
        await _context.SaveChangesAsync();
    }

    public async Task UpdateVibrationAsync(bool vibrationEnabled)
    {
        var setting = await GetOrCreateSettingAsync();
        setting.VibrationEnabled = vibrationEnabled;
        await _context.SaveChangesAsync();
    }

    public async Task SelectPeriodAsync(string selectedMonth, int selectedYear)
    {
        var setting = await GetOrCreateSettingAsync();
        setting.SelectedMonth = selectedMonth;
        setting.SelectedYear = selectedYear;
        await _context.SaveChangesAsync();
    }

    private async Task<FinancialSetting> GetOrCreateSettingAsync()
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
        if (setting == null)
        {
            setting = new FinancialSetting();
            _context.FinancialSettings.Add(setting);
        }
        return setting;
    }

    // Shared by GetDashboardDataAsync and GetDashboardInsightsAsync: loads (or creates) the
    // settings row and resolves which cycle is "active" -- either the caller's explicit
    // month/year, or today's detected cycle when omitted. `persist` controls whether the
    // resolution is written back as the new SelectedMonth/SelectedYear: the dashboard endpoint
    // stays the writer of record (persist: true, preserving its existing behavior exactly),
    // while insights is a pure reader (persist: false) so the two endpoints -- fetched in
    // parallel by the frontend with the same explicit month/year -- don't both redundantly
    // re-save the identical values.
    private async Task<(FinancialSetting setting, int cycleDay, string activeMonth, int activeYear, int activeMonthIndex)>
        ResolveCycleContextAsync(string? queryMonth, int? queryYear, bool persist)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
        if (setting == null)
        {
            var now = _financialClock.LocalNow;
            setting = new FinancialSetting
            {
                TargetStabilityFund = 10000.00m,
                SelectedMonth = now.ToString("MMM"),
                SelectedYear = now.Year,
                EssentialsAlloc = 0.50m,
                GrowthAlloc = 0.25m,
                StabilityAlloc = 0.15m,
                RewardsAlloc = 0.10m,
                CycleDay = 28,
                HideSensitive = true
            };
            _context.FinancialSettings.Add(setting);
            await _context.SaveChangesAsync();
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

        if (persist)
        {
            setting.SelectedMonth = activeMonth;
            setting.SelectedYear = activeYear;
            await _context.SaveChangesAsync();
        }

        var activeMonthIndex = Array.IndexOf(Months, activeMonth) + 1;
        if (activeMonthIndex == 0) throw new ArgumentException("Invalid month.", nameof(queryMonth));

        return (setting, cycleDay, activeMonth, activeYear, activeMonthIndex);
    }

    private async Task<List<Transaction>> GetTransactionsForCycleAsync(int year, int monthIndex, int cycleDay)
    {
        var (start, end, _) = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);
        var startDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(start));
        var endExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(end));
        return await _context.Transactions
            .AsNoTracking()
            .Where(t => t.Date >= startDate && t.Date < endExclusive)
            .ToListAsync();
    }

    private List<object> BuildActiveRecurringList(
        List<RecurringPayment> allRecurring,
        List<Transaction> activeCycleTxs,
        DateTime activeRangeStart,
        DateTime activeRangeEnd,
        int cycleDay,
        int activeYear,
        int activeMonthIndex)
    {
        var activeRecurringList = new List<object>();
        foreach (var rp in allRecurring)
        {
            if (!rp.Active) continue;
            foreach (var billingDate in _recurringOccurrenceService.GetOccurrencesInRange(
                         rp,
                         activeRangeStart,
                         activeRangeEnd,
                         cycleDay))
            {
                var instanceId = $"{rp.Id}-{activeYear}-{activeMonthIndex}";
                var paidTx = activeCycleTxs
                    .Where(t => t.RecurringPaymentId == rp.Id)
                    .OrderBy(t => string.Equals(t.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(t => t.Date)
                    .ThenBy(t => t.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
                var isDiscarded = paidTx != null && string.Equals(paidTx.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase);
                var isPaid = paidTx != null && !isDiscarded;

                activeRecurringList.Add(new
                {
                    id = instanceId,
                    recurringPaymentId = rp.Id,
                    name = rp.Name,
                    amount = ObfuscationHelper.Obfuscate(Math.Abs(rp.Amount)),
                    category = rp.Category,
                    ledgerCategory = rp.LedgerCategory,
                    dueDate = billingDate.ToString("yyyy-MM-dd"),
                    isPaid = isPaid,
                    isDiscarded = isDiscarded,
                    status = isDiscarded ? "Discarded" : (isPaid ? "Paid" : "Pending"),
                    paidDate = isPaid && !isDiscarded ? TransactionDate.ToDateOnly(paidTx!.Date).ToString("yyyy-MM-dd") : null
                });
            }
        }

        return activeRecurringList;
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

    private async Task<List<int>> GetAvailableYearsAsync()
    {
        var minYear = await _context.Transactions
            .OrderBy(t => t.Date)
            .Select(t => t.Date.Year)
            .FirstOrDefaultAsync();
        var maxYear = await _context.Transactions
            .OrderByDescending(t => t.Date)
            .Select(t => t.Date.Year)
            .FirstOrDefaultAsync();
        var currentYear = _financialClock.LocalNow.Year;

        if (minYear == 0 || maxYear == 0)
        {
            return [currentYear];
        }

        minYear = Math.Min(minYear, currentYear);
        maxYear = Math.Max(maxYear, currentYear);

        return Enumerable.Range(minYear, maxYear - minYear + 1).ToList();
    }

    private static List<(string category, decimal amount)> BuildBreakdown(List<Transaction> txs) => txs
        .Where(t => t.Amount < 0 && !IsTransfer(t))
        .GroupBy(t => string.IsNullOrWhiteSpace(t.Category) ? "Other" : t.Category)
        .Select(g => (category: g.Key, amount: Math.Abs(g.Sum(t => t.Amount))))
        .OrderByDescending(b => b.amount)
        .ToList();

    private static bool IsTransfer(Transaction transaction) =>
        string.Equals(transaction.Category, "Transfer", StringComparison.OrdinalIgnoreCase) ||
        transaction.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase);

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

    private static List<object> ObfuscateTrendPoints(List<(string month, decimal balance)> points) =>
        points.Select(p => (object)new { month = p.month, balance = ObfuscationHelper.Obfuscate(p.balance) }).ToList();

    private static List<object> ObfuscateBreakdown(List<(string category, decimal amount)> breakdown) =>
        breakdown.Select(b => (object)new { category = b.category, amount = ObfuscationHelper.Obfuscate(b.amount) }).ToList();
}
