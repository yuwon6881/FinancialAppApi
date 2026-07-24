using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using FinancialAppApi.Diagnostics;
using FinancialAppApi.Services.Investments;

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

    public async Task<object> GetDashboardDataAsync(
        string? queryMonth = null,
        int? queryYear = null,
        bool persistSelection = true,
        bool summaryOnly = false)
    {
        var stopwatch = Stopwatch.StartNew();
        using var activity = Telemetry.ActivitySource.StartActivity("FinancialService.GetDashboardData");

        var (setting, cycleDay, _, activeYear, activeMonthIndex) =
            await ResolveCycleContextAsync(queryMonth, queryYear, persist: persistSelection);

        var allRecurring = await _context.RecurringPayments.AsNoTracking().Where(r => r.Active).ToListAsync();

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

        // Net change mixes allocated income, transfers, and expenses, so it cannot be used as a
        // proxy for "spent" in cycle reports. Report true purchase/payment outflows separately;
        // transfers only move money between envelopes and are intentionally excluded.
        var spentByCategory = activeCycleTxs
            .Where(t => t.Amount < 0 && !IsTransfer(t))
            .GroupBy(t => t.LedgerCategory, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Math.Abs(g.Sum(t => t.Amount)), StringComparer.OrdinalIgnoreCase);

        var selectedSpentEssentials = spentByCategory.GetValueOrDefault("Essentials");
        var selectedSpentGrowth = spentByCategory.GetValueOrDefault("Growth");
        var selectedSpentStability = spentByCategory.GetValueOrDefault("Stability");
        var selectedSpentRewards = spentByCategory.GetValueOrDefault("Rewards");

        var selectedRemEssentials = selectedBudgetEssentials + selectedNetEssentials;
        var selectedRemGrowth = selectedBudgetGrowth + selectedNetGrowth;
        var selectedRemStability = selectedBudgetStability + selectedNetStability;
        var selectedRemRewards = selectedBudgetRewards + selectedNetRewards;

        var trendPoints = new List<(string month, decimal balance)>();
        if (!summaryOnly)
        {
            await _cycleBalanceService.EnsureComputedThroughAsync(year, activeMonthIndex, cycleDay);
            var trendRows = await _context.CycleBalances
                .AsNoTracking()
                .Where(b => b.Year == activeYear && b.MonthIndex <= activeMonthIndex)
                .OrderBy(b => b.MonthIndex)
                .ToListAsync();
            trendPoints = trendRows.Select(r => (Months[r.MonthIndex - 1], r.GrowthBalance)).ToList();
        }

        var selectedCycleIncome = activeCycleTxs
            .Where(t => t.LedgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase) || string.Equals(t.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase))
            .Sum(t => t.Amount);

        var targetEssentials = selectedCycleIncome * setting.EssentialsAlloc;
        var targetGrowth = selectedCycleIncome * setting.GrowthAlloc;
        var targetStability = selectedCycleIncome * setting.StabilityAlloc;
        var targetRewards = selectedCycleIncome * setting.RewardsAlloc;

        var categories = new[]
        {
            new { name = "Essentials", allocation = setting.EssentialsAlloc, target = ObfuscationHelper.Obfuscate(targetEssentials), budget = ObfuscationHelper.Obfuscate(selectedBudgetEssentials), netChange = ObfuscationHelper.Obfuscate(selectedNetEssentials), spent = ObfuscationHelper.Obfuscate(selectedSpentEssentials), remaining = ObfuscationHelper.Obfuscate(selectedRemEssentials) },
            new { name = "Growth", allocation = setting.GrowthAlloc, target = ObfuscationHelper.Obfuscate(targetGrowth), budget = ObfuscationHelper.Obfuscate(selectedBudgetGrowth), netChange = ObfuscationHelper.Obfuscate(selectedNetGrowth), spent = ObfuscationHelper.Obfuscate(selectedSpentGrowth), remaining = ObfuscationHelper.Obfuscate(selectedRemGrowth) },
            new { name = "Stability", allocation = setting.StabilityAlloc, target = ObfuscationHelper.Obfuscate(targetStability), budget = ObfuscationHelper.Obfuscate(selectedBudgetStability), netChange = ObfuscationHelper.Obfuscate(selectedNetStability), spent = ObfuscationHelper.Obfuscate(selectedSpentStability), remaining = ObfuscationHelper.Obfuscate(selectedRemStability) },
            new { name = "Rewards", allocation = setting.RewardsAlloc, target = ObfuscationHelper.Obfuscate(targetRewards), budget = ObfuscationHelper.Obfuscate(selectedBudgetRewards), netChange = ObfuscationHelper.Obfuscate(selectedNetRewards), spent = ObfuscationHelper.Obfuscate(selectedSpentRewards), remaining = ObfuscationHelper.Obfuscate(selectedRemRewards) }
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

        var pendingRecurring = BuildPendingRecurringItems(
            allRecurring,
            activeCycleTxs,
            activeRange.start,
            activeRange.end,
            cycleDay);

        var todayPlanInsights = BuildTodayPlanInsights(
            activeCycleTxs,
            pendingRecurring,
            selectedRemEssentials,
            activeRange.start,
            activeRange.end);

        var categoryLimitProgress = await BuildCategoryLimitProgressAsync(
            activeCycleTxs,
            pendingRecurring,
            activeYear,
            activeMonthIndex,
            activeRange.start,
            activeRange.end);

        var pendingNotifications = summaryOnly
            ? new List<object>()
            : await _recurringPaymentAlertService.GetSubscriptionAlertsAsync();

        var monthlyCategoryBreakdown = BuildBreakdown(activeCycleTxs);

        var trendPointsList = trendPoints.ToList();
        var last6TrendPoints = trendPointsList.Count >= 6
            ? trendPointsList.GetRange(trendPointsList.Count - 6, 6)
            : trendPointsList.ToList();
        var last3TrendPoints = trendPointsList.Count >= 3
            ? trendPointsList.GetRange(trendPointsList.Count - 3, 3)
            : trendPointsList.ToList();

        var cycleSummaryInsights = BuildSummaryInsights(activeCycleTxs, activeRangeStartDate, activeRangeEndExclusive);

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
                stabilityOverflowRedirect = setting.StabilityOverflowRedirect,
                lastSummaryCycleSeen = setting.LastSummaryCycleSeen
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
            monthlyCategoryBreakdown = ObfuscateBreakdown(monthlyCategoryBreakdown),
            todayPlanInsights,
            categoryLimitProgress,
            cycleSummaryInsights
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

        // Yearly breakdown only ever aggregated by category across the whole range. Push the
        // GroupBy/Sum into SQL so a full year of expense rows collapses to one row per category
        // in the database, instead of materializing every yearly transaction just to group it in
        // memory. The projection mirrors BuildBreakdown's semantics exactly (exclude transfers,
        // sum outflows by category, abs, order by amount desc).
        var yearStartDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(CategoryAttributionService.GetCycleRange(activeYear, 1, cycleDay).start));
        var yearEndExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(CategoryAttributionService.GetCycleRange(activeYear, 12, cycleDay).end));
        var yearlyGroups = await _context.Transactions
            .AsNoTracking()
            .Where(t => t.Date >= yearStartDate && t.Date < yearEndExclusive)
            .Where(t => t.Amount < 0)
            .Where(t => t.Category.ToLower() != "transfer")
            .Where(t => !t.LedgerCategory.ToLower().StartsWith("transfer:"))
            .GroupBy(t => t.Category)
            .Select(g => new { Category = g.Key, Total = g.Sum(t => t.Amount) })
            .ToListAsync();
        var yearlyCategoryBreakdown = yearlyGroups
            .GroupBy(g => string.IsNullOrWhiteSpace(g.Category) ? "Other" : g.Category)
            .Select(g => (category: g.Key, amount: Math.Abs(g.Sum(x => x.Total))))
            .OrderByDescending(b => b.amount)
            .ToList();

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
        if (!CurrencyCatalog.Contains(update.Currency))
        {
            return "Select a supported currency from the list.";
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
        setting.Currency = update.Currency.Trim().ToUpperInvariant();

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

    // Records which cycle the user has acknowledged an end-of-cycle summary for. Kept as a
    // tiny dedicated writer (like dark-mode/hide-sensitive) so acknowledging a summary never
    // races or overwrites a full settings edit. A null/blank key clears the marker.
    public async Task UpdateSummarySeenAsync(string? cycleKey)
    {
        var setting = await GetOrCreateSettingAsync();
        setting.LastSummaryCycleSeen = string.IsNullOrWhiteSpace(cycleKey) ? null : cycleKey.Trim();
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
                HideSensitive = false
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

        if (persist && (setting.SelectedMonth != activeMonth || setting.SelectedYear != activeYear))
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
        var txsByRecurringId = activeCycleTxs
            .Where(t => t.RecurringPaymentId != null)
            .GroupBy(t => t.RecurringPaymentId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var activeRecurringList = new List<object>();
        foreach (var rp in allRecurring)
        {
            foreach (var billingDate in _recurringOccurrenceService.GetOccurrencesInRange(
                         rp,
                         activeRangeStart,
                         activeRangeEnd,
                         cycleDay))
            {
                var instanceId = $"{rp.Id}-{activeYear}-{activeMonthIndex}";
                var relatedTxs = txsByRecurringId.GetValueOrDefault(rp.Id);
                var paidTx = relatedTxs?
                    .OrderBy(t => string.Equals(t.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(t => t.Date)
                    .ThenBy(t => t.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
                var isDiscarded = paidTx != null && string.Equals(paidTx.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase);
                var isPaid = paidTx != null && !isDiscarded;

                // The recurring payment is a template for pending and future occurrences. Once an
                // occurrence is paid, its linked ledger transaction is the historical snapshot:
                // later subscription edits must not rewrite what the user actually recorded for
                // this cycle. Discard markers carry no payment details, so they retain the template
                // fields while reporting their discarded status.
                activeRecurringList.Add(new
                {
                    id = instanceId,
                    recurringPaymentId = rp.Id,
                    name = isPaid ? paidTx!.Description : rp.Name,
                    amount = ObfuscationHelper.Obfuscate(isPaid ? Math.Abs(paidTx!.Amount) : Math.Abs(rp.Amount)),
                    category = isPaid ? paidTx!.Category : rp.Category,
                    ledgerCategory = isPaid ? paidTx!.LedgerCategory : rp.LedgerCategory,
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
        var yearRange = await _context.Transactions
            .GroupBy(_ => 1)
            .Select(g => new { Min = g.Min(t => t.Date.Year), Max = g.Max(t => t.Date.Year) })
            .FirstOrDefaultAsync();

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

    private sealed record PendingRecurringItem(string Category, string LedgerCategory, decimal Amount);

    private List<PendingRecurringItem> BuildPendingRecurringItems(
        List<RecurringPayment> allRecurring,
        List<Transaction> activeCycleTxs,
        DateTime rangeStart,
        DateTime rangeEnd,
        int cycleDay)
    {
        var resolvedRecurringIds = activeCycleTxs
            .Where(transaction => !string.IsNullOrWhiteSpace(transaction.RecurringPaymentId))
            .Select(transaction => transaction.RecurringPaymentId!)
            .ToHashSet(StringComparer.Ordinal);
        var pending = new List<PendingRecurringItem>();

        foreach (var payment in allRecurring)
        {
            if (resolvedRecurringIds.Contains(payment.Id)) continue;
            foreach (var _ in _recurringOccurrenceService.GetOccurrencesInRange(payment, rangeStart, rangeEnd, cycleDay))
            {
                pending.Add(new PendingRecurringItem(
                    payment.Category,
                    payment.LedgerCategory,
                    Math.Abs(payment.Amount)));
            }
        }

        return pending;
    }

    private object BuildTodayPlanInsights(
        List<Transaction> activeCycleTxs,
        List<PendingRecurringItem> pendingRecurring,
        decimal essentialsRemaining,
        DateTime rangeStart,
        DateTime rangeEnd)
    {
        var nonRecurringEssentialsSpent = Math.Abs(activeCycleTxs
            .Where(transaction =>
                transaction.Amount < 0 &&
                !IsTransfer(transaction) &&
                string.IsNullOrWhiteSpace(transaction.RecurringPaymentId) &&
                !string.Equals(transaction.Category, "Adjustment", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(transaction.LedgerCategory, "Essentials", StringComparison.OrdinalIgnoreCase))
            .Sum(transaction => transaction.Amount));

        var start = DateOnly.FromDateTime(rangeStart);
        var end = DateOnly.FromDateTime(rangeEnd);
        var today = _financialClock.Today;
        var totalDays = end.DayNumber - start.DayNumber + 1;
        var elapsedDays = today < start
            ? 0
            : today > end
                ? totalDays
                : today.DayNumber - start.DayNumber + 1;
        var remainingDaysAfterToday = today < start
            ? totalDays
            : today > end
                ? 0
                : Math.Max(0, totalDays - elapsedDays);
        var dailyAverage = elapsedDays > 0 ? nonRecurringEssentialsSpent / elapsedDays : 0m;
        var unpaidEssentials = pendingRecurring
            .Where(item => string.Equals(item.LedgerCategory, "Essentials", StringComparison.OrdinalIgnoreCase))
            .Sum(item => item.Amount);
        var projectedEndingBalance = today > end
            ? essentialsRemaining
            : essentialsRemaining - unpaidEssentials - (dailyAverage * remainingDaysAfterToday);

        return new
        {
            unpaidRecurringCount = pendingRecurring.Count,
            unpaidRecurringTotal = ObfuscationHelper.Obfuscate(pendingRecurring.Sum(item => item.Amount)),
            unpaidEssentialsTotal = ObfuscationHelper.Obfuscate(unpaidEssentials),
            nonRecurringEssentialsSpent = ObfuscationHelper.Obfuscate(nonRecurringEssentialsSpent),
            nonRecurringEssentialsDailyAverage = ObfuscationHelper.Obfuscate(dailyAverage),
            projectedEssentialsEndingBalance = ObfuscationHelper.Obfuscate(projectedEndingBalance)
        };
    }

    private async Task<List<object>> BuildCategoryLimitProgressAsync(
        List<Transaction> activeCycleTxs,
        List<PendingRecurringItem> pendingRecurring,
        int activeYear,
        int activeMonthIndex,
        DateTime rangeStart,
        DateTime rangeEnd)
    {
        var cycleKey = $"{activeYear:D4}-{activeMonthIndex:D2}";
        var allGuideVersions = await _context.CategorySpendingGuides
            .AsNoTracking()
            .ToListAsync();
        var effectiveGuides = allGuideVersions
            .Where(guide => string.CompareOrdinal(guide.EffectiveFromCycleKey, cycleKey) <= 0)
            .GroupBy(guide => guide.CategoryName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(guide => guide.EffectiveFromCycleKey).First())
            .Where(guide => guide.LimitAmount.HasValue)
            .OrderBy(guide => guide.CategoryName)
            .ToList();

        if (effectiveGuides.Count == 0) return [];

        var expenseTransactions = activeCycleTxs
            .Where(transaction =>
                transaction.Amount < 0 &&
                !IsTransfer(transaction) &&
                !string.Equals(transaction.Category, "Adjustment", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var spentByCategory = expenseTransactions
            .GroupBy(transaction => transaction.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => Math.Abs(group.Sum(transaction => transaction.Amount)), StringComparer.OrdinalIgnoreCase);
        var recurringSpentByCategory = expenseTransactions
            .Where(transaction => !string.IsNullOrWhiteSpace(transaction.RecurringPaymentId))
            .GroupBy(transaction => transaction.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => Math.Abs(group.Sum(transaction => transaction.Amount)), StringComparer.OrdinalIgnoreCase);
        var nonRecurringSpentByCategory = expenseTransactions
            .Where(transaction => string.IsNullOrWhiteSpace(transaction.RecurringPaymentId))
            .GroupBy(transaction => transaction.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => Math.Abs(group.Sum(transaction => transaction.Amount)), StringComparer.OrdinalIgnoreCase);
        var pendingByCategory = pendingRecurring
            .GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Amount), StringComparer.OrdinalIgnoreCase);

        var start = DateOnly.FromDateTime(rangeStart);
        var end = DateOnly.FromDateTime(rangeEnd);
        var today = _financialClock.Today;
        var totalDays = end.DayNumber - start.DayNumber + 1;
        var elapsedDays = today < start
            ? 0
            : today > end
                ? totalDays
                : today.DayNumber - start.DayNumber + 1;
        var isEnded = today > end;

        return effectiveGuides.Select(guide =>
        {
            var limit = guide.LimitAmount!.Value;
            var spent = spentByCategory.GetValueOrDefault(guide.CategoryName);
            var recurringSpent = recurringSpentByCategory.GetValueOrDefault(guide.CategoryName);
            var nonRecurringSpent = nonRecurringSpentByCategory.GetValueOrDefault(guide.CategoryName);
            var pending = pendingByCategory.GetValueOrDefault(guide.CategoryName);
            var projected = isEnded
                ? spent
                : recurringSpent + pending + (elapsedDays > 0 ? nonRecurringSpent / elapsedDays * totalDays : 0m);
            var status = spent > limit
                ? "Exceeded"
                : projected > limit
                    ? "Watch"
                    : "OnTrack";

            return (object)new
            {
                category = guide.CategoryName,
                limit = ObfuscationHelper.Obfuscate(limit),
                spent = ObfuscationHelper.Obfuscate(spent),
                remaining = ObfuscationHelper.Obfuscate(limit - spent),
                pendingCommitted = ObfuscationHelper.Obfuscate(pending),
                projectedSpend = ObfuscationHelper.Obfuscate(projected),
                percentUsed = limit > 0 ? (double)(spent / limit) : 0d,
                status
            };
        }).ToList();
    }

    private static object? BuildSummaryInsights(List<Transaction> activeCycleTxs, DateTime start, DateTime endExclusive)
    {
        var cycleExpenseTxs = activeCycleTxs.Where(t => t.Amount < 0 && !IsTransfer(t)).ToList();
        
        var largestTxn = cycleExpenseTxs.OrderBy(t => t.Amount).FirstOrDefault(); // Amount is negative, so smallest value is largest absolute amount
        
        var biggestDay = cycleExpenseTxs
            .GroupBy(t => TransactionDate.ToDateOnly(t.Date))
            .Select(g => new { Date = g.Key, Total = Math.Abs(g.Sum(t => t.Amount)) })
            .OrderByDescending(g => g.Total)
            .FirstOrDefault();
            
        var startMid = DateOnly.FromDateTime(start);
        var endMid = DateOnly.FromDateTime(endExclusive);
        var cycleLengthDays = endMid.DayNumber - startMid.DayNumber;
        
        var avgDailySpend = cycleExpenseTxs.Count > 0 && cycleLengthDays > 0
            ? Math.Abs(cycleExpenseTxs.Sum(t => t.Amount)) / cycleLengthDays
            : (decimal?)null;
            
        var startDateTime = start;
        var endDateTime = endExclusive;
        var midMs = startDateTime.Ticks + (endDateTime.Ticks - startDateTime.Ticks) / 2;
        
        decimal velocityFirstHalf = 0;
        decimal velocitySecondHalf = 0;
        
        foreach (var t in cycleExpenseTxs)
        {
            if (t.Date.Ticks <= midMs) velocityFirstHalf += Math.Abs(t.Amount);
            else velocitySecondHalf += Math.Abs(t.Amount);
        }
        
        var distinctExpenseDays = cycleExpenseTxs.Select(t => TransactionDate.ToDateOnly(t.Date)).Distinct().Count();
        var noSpendDays = Math.Max(0, cycleLengthDays - distinctExpenseDays);
        
        var transactionCount = cycleExpenseTxs.Count;
        
        var committedSpend = Math.Abs(cycleExpenseTxs.Where(t => !string.IsNullOrEmpty(t.RecurringPaymentId)).Sum(t => t.Amount));
        var discretionarySpend = Math.Abs(cycleExpenseTxs.Where(t => string.IsNullOrEmpty(t.RecurringPaymentId)).Sum(t => t.Amount));
        
        return new
        {
            largestExpenseDescription = largestTxn?.Description,
            largestExpenseAmount = largestTxn != null ? ObfuscationHelper.Obfuscate(Math.Abs(largestTxn.Amount)) : null,
            biggestDayDate = biggestDay?.Date.ToString("yyyy-MM-dd"),
            biggestDayTotal = biggestDay != null ? ObfuscationHelper.Obfuscate(biggestDay.Total) : null,
            avgDailySpend = avgDailySpend != null ? ObfuscationHelper.Obfuscate(avgDailySpend.Value) : null,
            cycleLengthDays,
            velocityFirstHalf = cycleExpenseTxs.Count > 0 ? ObfuscationHelper.Obfuscate(velocityFirstHalf) : null,
            velocitySecondHalf = cycleExpenseTxs.Count > 0 ? ObfuscationHelper.Obfuscate(velocitySecondHalf) : null,
            noSpendDays,
            transactionCount,
            committedSpend = ObfuscationHelper.Obfuscate(committedSpend),
            discretionarySpend = ObfuscationHelper.Obfuscate(discretionarySpend)
        };
    }
}
