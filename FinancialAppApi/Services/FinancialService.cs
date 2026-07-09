using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

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

    public FinancialService(
        AppDbContext context,
        CycleBalanceService cycleBalanceService,
        RecurringPaymentAlertService recurringPaymentAlertService)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService;
        _recurringPaymentAlertService = recurringPaymentAlertService;
    }

    public async Task<object> GetWalletBalanceAsync()
    {
        var setting = await GetOrCreateSettingAsync();
        var cycleDay = setting.CycleDay;

        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(DateOnly.FromDateTime(DateTime.Now), cycleDay);
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
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
        if (setting == null)
        {
            var now = DateTime.Now;
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
            var detected = CategoryAttributionService.GetCycleMonthAndYearForDate(DateTime.Now, cycleDay);
            activeMonth = detected.month;
            activeYear = detected.year;

            setting.SelectedMonth = activeMonth;
            setting.SelectedYear = activeYear;
            await _context.SaveChangesAsync();
        }
        else
        {
            activeMonth = queryMonth;
            activeYear = queryYear.Value;

            setting.SelectedMonth = activeMonth;
            setting.SelectedYear = activeYear;
            await _context.SaveChangesAsync();
        }

        var allRecurring = await _context.RecurringPayments.ToListAsync();

        var activeMonthIndex = Array.IndexOf(Months, activeMonth) + 1;
        if (activeMonthIndex == 0) activeMonthIndex = 6;

        var year = activeYear;

        var activeRange = CategoryAttributionService.GetCycleRange(year, activeMonthIndex, cycleDay);
        var activeRangeStartDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(activeRange.start));
        var activeRangeEndExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(activeRange.end));
        var activeCycleTxs = await _context.Transactions
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
        var monthlyInflow = activeCycleTxs.Where(t => t.Amount > 0 && !t.LedgerCategory.StartsWith("Transfer:")).Sum(t => t.Amount);
        var monthlyOutflow = Math.Abs(activeCycleTxs.Where(t => t.Amount < 0).Sum(t => t.Amount));
        var activeRecurringTotal = Math.Abs(allRecurring.Where(r => r.Active).Sum(r => r.Amount));
        var growthPercentAchieved = selectedNetGrowth / (targetGrowth > 0 ? targetGrowth : 1m);
        var essentialsPercentRemaining = targetEssentials > 0
            ? Math.Max(0m, selectedRemEssentials / targetEssentials)
            : 0m;
        var stabilityPercentReached = selectedRemStability / (setting.TargetStabilityFund > 0 ? setting.TargetStabilityFund : 1m);

        var recentTransactions = activeCycleTxs
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.Id)
            .Take(5)
            .Select(t => new
            {
                id = t.Id,
                date = TransactionDate.ToDateOnly(t.Date).ToString("yyyy-MM-dd"),
                postedAt = t.Date.ToUniversalTime().ToString("O"),
                description = t.Description,
                category = t.Category,
                ledgerCategory = t.LedgerCategory,
                amount = t.Amount
            })
            .ToList();

        var activeRecurringList = BuildActiveRecurringList(allRecurring, activeCycleTxs, activeRange.start, activeRange.end, cycleDay, activeYear, activeMonthIndex);
        var selectedMonthRecurring = activeRecurringList
            .OrderBy(r => ((dynamic)r).status == "Pending" ? 0 : ((dynamic)r).status == "Paid" ? 1 : 2)
            .ThenBy(r => ((dynamic)r).dueDate)
            .ToList();

        var pendingNotifications = await _recurringPaymentAlertService.GetSubscriptionAlertsAsync();

        var monthlyCategoryBreakdown = BuildBreakdown(activeCycleTxs);

        var yearlyTxs = new List<Transaction>();
        for (int m = 1; m <= 12; m++)
        {
            yearlyTxs.AddRange(await GetTransactionsForCycleAsync(activeYear, m, cycleDay));
        }

        var yearlyCategoryBreakdown = BuildBreakdown(yearlyTxs);
        var last3CategoryBreakdown = BuildBreakdown(await GetTxsForLastNCyclesAsync(activeYear, activeMonthIndex, cycleDay, 3));
        var last6CategoryBreakdown = BuildBreakdown(await GetTxsForLastNCyclesAsync(activeYear, activeMonthIndex, cycleDay, 6));

        var trendPointsList = trendPoints.ToList();
        var last6TrendPoints = trendPointsList.Count >= 6
            ? trendPointsList.GetRange(trendPointsList.Count - 6, 6)
            : trendPointsList.ToList();
        var last3TrendPoints = trendPointsList.Count >= 3
            ? trendPointsList.GetRange(trendPointsList.Count - 3, 3)
            : trendPointsList.ToList();

        var transactionYears = await _context.Transactions.Select(t => t.Date.Year).Distinct().ToListAsync();
        var availableYears = transactionYears
            .Append(DateTime.Now.Year)
            .Distinct()
            .OrderBy(y => y)
            .ToList();

        var (pastThreeMonthsRewardsAverage, hasRewardsHistory) =
            await CalculatePastRewardsAverageAsync(activeYear, activeMonthIndex, cycleDay);

        return new
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
                essentialsPercentRemaining = (double)essentialsPercentRemaining,
                stabilityPercentReached = (double)Math.Max(0, stabilityPercentReached),
                pastThreeMonthsRewardsAverage = ObfuscationHelper.Obfuscate(pastThreeMonthsRewardsAverage),
                hasRewardsHistory = hasRewardsHistory
            },
            recentTransactions = recentTransactions.Select(t => new
            {
                id = t.id,
                date = t.date,
                postedAt = t.postedAt,
                description = t.description,
                category = t.category,
                ledgerCategory = t.ledgerCategory,
                amount = ObfuscationHelper.Obfuscate(t.amount)
            }).ToList(),
            activeRecurringPayments = selectedMonthRecurring,
            trendPoints = ObfuscateTrendPoints(trendPoints),
            last3TrendPoints = ObfuscateTrendPoints(last3TrendPoints),
            last6TrendPoints = ObfuscateTrendPoints(last6TrendPoints),
            pendingNotifications,
            monthlyCategoryBreakdown = ObfuscateBreakdown(monthlyCategoryBreakdown),
            last3CategoryBreakdown = ObfuscateBreakdown(last3CategoryBreakdown),
            last6CategoryBreakdown = ObfuscateBreakdown(last6CategoryBreakdown),
            yearlyCategoryBreakdown = ObfuscateBreakdown(yearlyCategoryBreakdown),
            availableYears
        };
    }

    public async Task UpdateSettingsAsync(FinancialSettingsUpdate update)
    {
        var setting = await GetOrCreateSettingAsync();

        setting.TargetStabilityFund = ObfuscationHelper.Deobfuscate(update.TargetStabilityFund);
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

    private async Task<List<Transaction>> GetTransactionsForCycleAsync(int year, int monthIndex, int cycleDay)
    {
        var (start, end, _) = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);
        var startDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(start));
        var endExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(end));
        return await _context.Transactions
            .Where(t => t.Date >= startDate && t.Date < endExclusive)
            .ToListAsync();
    }

    private static List<object> BuildActiveRecurringList(
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
            if (!DateTime.TryParse(rp.StartDate, out var rpStartDate)) continue;
            DateTime? rpEndDate = null;
            if (!string.IsNullOrEmpty(rp.EndDate) && DateTime.TryParse(rp.EndDate, out var parsedEndDate))
            {
                rpEndDate = parsedEndDate;
            }

            var billingDate = CategoryAttributionService.GetBillingDateForCycle(activeRangeStart, activeRangeEnd, cycleDay, rp.DueDate);

            if (billingDate >= activeRangeStart && billingDate <= activeRangeEnd && billingDate >= rpStartDate && (rpEndDate == null || billingDate <= rpEndDate.Value))
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

    private async Task<List<Transaction>> GetTxsForLastNCyclesAsync(int activeYear, int activeMonthIndex, int cycleDay, int n)
    {
        var result = new List<Transaction>();
        int curMonth = activeMonthIndex;
        int curYear = activeYear;
        for (int i = 0; i < n; i++)
        {
            result.AddRange(await GetTransactionsForCycleAsync(curYear, curMonth, cycleDay));
            curMonth--;
            if (curMonth < 1) { curMonth = 12; curYear--; }
        }
        return result;
    }

    private async Task<(decimal average, bool hasHistory)> CalculatePastRewardsAverageAsync(int activeYear, int activeMonthIndex, int cycleDay)
    {
        decimal totalPastRewards = 0;
        int activeMonthsCount = 0;
        int tempMonth = activeMonthIndex;
        int tempYear = activeYear;

        for (int i = 0; i < 3; i++)
        {
            var cycleTxsForMonth = await GetTransactionsForCycleAsync(tempYear, tempMonth, cycleDay);

            if (cycleTxsForMonth.Any())
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

    private static List<(string category, decimal amount)> BuildBreakdown(List<Transaction> txs) => txs
        .Where(t => t.Amount < 0)
        .GroupBy(t => string.IsNullOrWhiteSpace(t.Category) ? "Other" : t.Category)
        .Select(g => (category: g.Key, amount: Math.Abs(g.Sum(t => t.Amount))))
        .OrderByDescending(b => b.amount)
        .ToList();

    private static List<object> ObfuscateTrendPoints(List<(string month, decimal balance)> points) =>
        points.Select(p => (object)new { month = p.month, balance = ObfuscationHelper.Obfuscate(p.balance) }).ToList();

    private static List<object> ObfuscateBreakdown(List<(string category, decimal amount)> breakdown) =>
        breakdown.Select(b => (object)new { category = b.category, amount = ObfuscationHelper.Obfuscate(b.amount) }).ToList();
}
