using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Accounts;
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
    string Currency,
    string? StabilityOverflowRedirect);

public partial class FinancialService
{
    private static readonly string[] Months = FinancialConstants.MonthAbbreviations;

    internal sealed record FinancialCycleContext(
        FinancialSetting Setting,
        int CycleDay,
        string ActiveMonth,
        int ActiveYear,
        int ActiveMonthIndex);

    internal sealed record FinancialBootstrapSnapshot(
        FinancialCycleContext Cycle,
        List<RecurringPayment> ActiveRecurringPayments,
        List<RecurringPaymentOccurrence> RecurringOccurrences,
        List<Transaction> CycleRelevantTransactions,
        IReadOnlyList<LedgerAccount> LedgerAccounts,
        LedgerAccountBalanceSnapshot LedgerAccountBalances);

    private sealed record ActiveRecurringItem(
        string id,
        string? recurringPaymentId,
        string name,
        string? amount,
        string category,
        string ledgerCategory,
        string dueDate,
        bool isPaid,
        bool isDiscarded,
        string status,
        string? paidDate,
        string? scheduledAmount = null,
        string? paidAmount = null,
        string? remainingAmount = null);

    private sealed record ReportTrendPoint(string CycleKey, string Month, decimal Balance);
    private sealed record ReportCategoryTotal(string Category, decimal Total);

    private readonly AppDbContext _context;
    private readonly CycleBalanceService _cycleBalanceService;
    private readonly RecurringPaymentAlertService _recurringPaymentAlertService;
    private readonly RecurringOccurrenceLedgerService _recurringOccurrenceLedger;
    private readonly FinancialClock _financialClock;
    private readonly Stability.StabilityRecoveryService _stabilityRecoveryService;
    private readonly Stability.StabilityPlanRevisionService _stabilityPlanRevisionService;
    private readonly LedgerAccountService _ledgerAccountService;

    public FinancialService(
        AppDbContext context,
        CycleBalanceService cycleBalanceService,
        RecurringPaymentAlertService recurringPaymentAlertService,
        RecurringOccurrenceService recurringOccurrenceService,
        FinancialClock? financialClock = null,
        Stability.StabilityRecoveryService? stabilityRecoveryService = null,
        RecurringOccurrenceLedgerService? recurringOccurrenceLedger = null,
        Stability.StabilityPlanRevisionService? stabilityPlanRevisionService = null,
        LedgerAccountService? ledgerAccountService = null)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService;
        _recurringPaymentAlertService = recurringPaymentAlertService;
        _financialClock = financialClock ?? FinancialClock.Utc;
        _recurringOccurrenceLedger = recurringOccurrenceLedger
            ?? new RecurringOccurrenceLedgerService(context, recurringOccurrenceService, _financialClock);
        _stabilityRecoveryService = stabilityRecoveryService
            ?? new Stability.StabilityRecoveryService(context, cycleBalanceService, _financialClock);
        _stabilityPlanRevisionService = stabilityPlanRevisionService
            ?? new Stability.StabilityPlanRevisionService(context);
        _ledgerAccountService = ledgerAccountService
            ?? new LedgerAccountService(
                context,
                new LedgerAccountBalanceService(context, cycleBalanceService, _financialClock),
                cycleBalanceService,
                _financialClock);
    }

    public async Task<object> GetWalletBalanceAsync(CancellationToken cancellationToken = default)
    {
        var setting = await GetOrCreateSettingAsync(cancellationToken);

        return await BuildWalletBalanceAsync(setting, null, cancellationToken);
    }

    internal Task<object> GetWalletBalanceAsync(
        FinancialBootstrapSnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        BuildWalletBalanceAsync(snapshot.Cycle.Setting, snapshot, cancellationToken);

    private async Task<object> BuildWalletBalanceAsync(
        FinancialSetting setting,
        FinancialBootstrapSnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        var cycleDay = setting.CycleDay;

        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(_financialClock.Today, cycleDay);
        var currentCycleTxs = snapshot is not null &&
            snapshot.Cycle.ActiveYear == year &&
            snapshot.Cycle.ActiveMonthIndex == monthIndex
                ? GetActiveCycleTransactions(snapshot)
                : await GetTransactionsForCycleAsync(year, monthIndex, cycleDay, cancellationToken);

        var (budgetEssentials, _, budgetStability, budgetRewards) =
            await _cycleBalanceService.GetOpeningBalanceAsync(
                year,
                monthIndex,
                cycleDay,
                cancellationToken);

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
        bool summaryOnly = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await CreateBootstrapSnapshotAsync(
            queryMonth,
            queryYear,
            persistSelection,
            cancellationToken);
        return await GetDashboardDataAsync(snapshot, summaryOnly, cancellationToken);
    }

    internal async Task<FinancialBootstrapSnapshot> CreateBootstrapSnapshotAsync(
        string? queryMonth,
        int? queryYear,
        bool persistSelection,
        CancellationToken cancellationToken)
    {
        var cycle = await ResolveCycleContextAsync(
            queryMonth,
            queryYear,
            persistSelection,
            cancellationToken);
        // Load accounts once and reuse them for the dashboard totals and the account rows, so the
        // bootstrap pays a single account query rather than one per consumer.
        var ledgerAccounts = await _ledgerAccountService.GetAccountsAsync(cancellationToken);
        var activeRecurringPayments = await _context.RecurringPayments
            .AsNoTracking()
            .Where(payment => payment.Active)
            .ToListAsync(cancellationToken);
        var activeRange = CategoryAttributionService.GetCycleRange(
            cycle.ActiveYear,
            cycle.ActiveMonthIndex,
            cycle.CycleDay);
        var activeRangeStartDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(activeRange.start));
        var activeRangeEndExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(activeRange.end));
        var activeRangeStartOnly = DateOnly.FromDateTime(activeRange.start);
        var activeRangeEndOnly = DateOnly.FromDateTime(activeRange.end);
        var cycleRelevantTransactions = await _context.Transactions
            .AsNoTracking()
            .Where(transaction =>
                (transaction.Date >= activeRangeStartDate && transaction.Date < activeRangeEndExclusive)
                || (transaction.RecurringPaymentId != null
                    && transaction.RecurringOccurrenceDate != null
                    && transaction.RecurringOccurrenceDate >= activeRangeStartOnly
                    && transaction.RecurringOccurrenceDate <= activeRangeEndOnly))
            .ToListAsync(cancellationToken);

        var recurringOccurrences = await _recurringOccurrenceLedger.GetRangeAsync(
            activeRecurringPayments,
            activeRangeStartOnly,
            activeRangeEndOnly,
            cycleRelevantTransactions,
            cancellationToken);

        var ledgerAccountBalances = await _ledgerAccountService.GetBalanceSnapshotAsync(
            ledgerAccounts,
            activeRangeEndExclusive,
            cancellationToken,
            cycle.CycleDay);

        return new FinancialBootstrapSnapshot(
            cycle,
            activeRecurringPayments,
            recurringOccurrences,
            cycleRelevantTransactions,
            ledgerAccounts,
            ledgerAccountBalances);
    }

    internal async Task<object> GetDashboardDataAsync(
        FinancialBootstrapSnapshot snapshot,
        bool summaryOnly,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var activity = Telemetry.ActivitySource.StartActivity("FinancialService.GetDashboardData");

        var setting = snapshot.Cycle.Setting;
        var cycleDay = snapshot.Cycle.CycleDay;
        var activeYear = snapshot.Cycle.ActiveYear;
        var activeMonthIndex = snapshot.Cycle.ActiveMonthIndex;
        var allRecurring = snapshot.ActiveRecurringPayments;

        var year = activeYear;

        var activeRange = CategoryAttributionService.GetCycleRange(year, activeMonthIndex, cycleDay);
        var activeRangeStartDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(activeRange.start));
        var activeRangeEndExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(activeRange.end));
        // A transaction "settles" whichever recurring occurrence its RecurringOccurrenceDate names,
        // which is not always the cycle its own posting Date falls in -- a pay-early payment is
        // posted today but can settle a future cycle's occurrence. Matching recurring-payment status
        // by posting-date range (like activeCycleTxs below) would wrongly attribute it to today's
        // cycle instead. So two sets are needed: transactions posted in this cycle, and
        // transactions whose RecurringOccurrenceDate falls in this cycle.
        //
        // They are fetched in ONE query with an OR predicate and partitioned in memory rather
        // than as two round trips. The two sets overlap heavily (a normally-paid recurring
        // charge is in both), so a single scan serves both and the dashboard makes one fewer
        // round trip to Cloud SQL. The partition predicates below are deliberately identical to
        // the two halves of the OR.
        var activeRangeStartOnly = DateOnly.FromDateTime(activeRange.start);
        var activeRangeEndOnly = DateOnly.FromDateTime(activeRange.end);
        var cycleRelevantTxs = snapshot.CycleRelevantTransactions;

        var activeCycleTxs = cycleRelevantTxs
            .Where(t => t.Date >= activeRangeStartDate && t.Date < activeRangeEndExclusive)
            .ToList();
        string selectedCycleLabel = activeRange.label;

        var (selectedBudgetEssentials, selectedBudgetGrowth, selectedBudgetStability, selectedBudgetRewards) =
            await _cycleBalanceService.GetOpeningBalanceAsync(
                year,
                activeMonthIndex,
                cycleDay,
                cancellationToken);

        var selectedNetEssentials = activeCycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, "Essentials"));
        var selectedNetGrowth = activeCycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, "Growth"));
        var selectedNetStability = activeCycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, "Stability"));
        var selectedNetRewards = activeCycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, "Rewards"));

        // Net change mixes allocated income, transfers, and expenses, so it cannot be used as a
        // proxy for "spent" in cycle reports. Report true purchase/payment outflows separately;
        // transfers only move money between envelopes and are intentionally excluded.
        var spentByCategory = activeCycleTxs
            .Where(TransactionReportSemantics.IsReportableOutflow)
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

        var trendPoints = new List<ReportTrendPoint>();
        var last3TrendPoints = new List<ReportTrendPoint>();
        var last6TrendPoints = new List<ReportTrendPoint>();
        if (!summaryOnly)
        {
            var (currentCycleYear, currentCycleMonthIndex) = CategoryAttributionService
                .GetCycleYearAndMonthIndexForDate(_financialClock.Today, cycleDay);
            var yearEndMonth = activeYear < currentCycleYear
                ? 12
                : activeYear == currentCycleYear
                    ? currentCycleMonthIndex
                    : 0;
            if (yearEndMonth > 0)
            {
                await _cycleBalanceService.EnsureComputedThroughAsync(
                    activeYear,
                    yearEndMonth,
                    cycleDay,
                    cancellationToken);
                var yearRows = await _context.CycleBalances
                    .AsNoTracking()
                    .Where(balance => balance.Year == activeYear && balance.MonthIndex <= yearEndMonth)
                    .OrderBy(balance => balance.MonthIndex)
                    .ToListAsync(cancellationToken);
                trendPoints = yearRows.Select(ToTrendPoint).ToList();
                var rollingEndMonth = activeYear == currentCycleYear
                    ? Math.Min(activeMonthIndex, currentCycleMonthIndex)
                    : activeMonthIndex;
                var selectedOrdinal = (activeYear * 12) + rollingEndMonth;
                var rollingRows = await _context.CycleBalances
                    .AsNoTracking()
                    .Where(balance =>
                        (balance.Year * 12) + balance.MonthIndex <= selectedOrdinal &&
                        (balance.Year * 12) + balance.MonthIndex > selectedOrdinal - 6)
                    .OrderBy(balance => balance.Year)
                    .ThenBy(balance => balance.MonthIndex)
                    .ToListAsync(cancellationToken);
                last6TrendPoints = rollingRows.Select(ToTrendPoint).ToList();
                last3TrendPoints = last6TrendPoints.TakeLast(3).ToList();
            }
        }

        var selectedCycleIncome = activeCycleTxs
            .Where(TransactionReportSemantics.IsReportableIncome)
            .Sum(t => t.Amount);

        var targetEssentials = selectedCycleIncome * setting.EssentialsAlloc;
        var targetGrowth = selectedCycleIncome * setting.GrowthAlloc;
        var targetStability = selectedCycleIncome * setting.StabilityAlloc;
        var targetRewards = selectedCycleIncome * setting.RewardsAlloc;

        var incomeAllocatedEssentials = ReportMetricsCalculator.IncomeAllocatedTo(activeCycleTxs, "Essentials");
        var incomeAllocatedGrowth = ReportMetricsCalculator.IncomeAllocatedTo(activeCycleTxs, "Growth");
        var incomeAllocatedStability = ReportMetricsCalculator.IncomeAllocatedTo(activeCycleTxs, "Stability");
        var incomeAllocatedRewards = ReportMetricsCalculator.IncomeAllocatedTo(activeCycleTxs, "Rewards");

        var ledgerAccounts = snapshot.LedgerAccounts;
        var ledgerAccountBalances = snapshot.LedgerAccountBalances.ThroughExclusive;
        object AccountsFor(string bucket) => ledgerAccounts
            .Where(account => string.Equals(account.Bucket, bucket, StringComparison.OrdinalIgnoreCase))
            .Select(account => new
            {
                id = account.Id,
                name = account.Name,
                kind = account.Kind,
                remaining = ObfuscationHelper.Obfuscate(
                    ledgerAccountBalances.TryGetValue(account.Id, out var balance) ? balance : 0m),
                isArchived = account.IsArchived,
            })
            .ToList();

        var categories = new[]
        {
            new { name = "Essentials", allocation = setting.EssentialsAlloc, target = ObfuscationHelper.Obfuscate(targetEssentials), incomeAllocated = ObfuscationHelper.Obfuscate(incomeAllocatedEssentials), budget = ObfuscationHelper.Obfuscate(selectedBudgetEssentials), netChange = ObfuscationHelper.Obfuscate(selectedNetEssentials), spent = ObfuscationHelper.Obfuscate(selectedSpentEssentials), remaining = ObfuscationHelper.Obfuscate(selectedRemEssentials), accounts = AccountsFor("Essentials") },
            new { name = "Growth", allocation = setting.GrowthAlloc, target = ObfuscationHelper.Obfuscate(targetGrowth), incomeAllocated = ObfuscationHelper.Obfuscate(incomeAllocatedGrowth), budget = ObfuscationHelper.Obfuscate(selectedBudgetGrowth), netChange = ObfuscationHelper.Obfuscate(selectedNetGrowth), spent = ObfuscationHelper.Obfuscate(selectedSpentGrowth), remaining = ObfuscationHelper.Obfuscate(selectedRemGrowth), accounts = AccountsFor("Growth") },
            new { name = "Stability", allocation = setting.StabilityAlloc, target = ObfuscationHelper.Obfuscate(targetStability), incomeAllocated = ObfuscationHelper.Obfuscate(incomeAllocatedStability), budget = ObfuscationHelper.Obfuscate(selectedBudgetStability), netChange = ObfuscationHelper.Obfuscate(selectedNetStability), spent = ObfuscationHelper.Obfuscate(selectedSpentStability), remaining = ObfuscationHelper.Obfuscate(selectedRemStability), accounts = AccountsFor("Stability") },
            new { name = "Rewards", allocation = setting.RewardsAlloc, target = ObfuscationHelper.Obfuscate(targetRewards), incomeAllocated = ObfuscationHelper.Obfuscate(incomeAllocatedRewards), budget = ObfuscationHelper.Obfuscate(selectedBudgetRewards), netChange = ObfuscationHelper.Obfuscate(selectedNetRewards), spent = ObfuscationHelper.Obfuscate(selectedSpentRewards), remaining = ObfuscationHelper.Obfuscate(selectedRemRewards), accounts = AccountsFor("Rewards") }
        };

        var totalBalance = selectedRemEssentials + selectedRemStability + selectedRemRewards;
        var monthlyInflow = activeCycleTxs.Where(TransactionReportSemantics.IsReportableInflow).Sum(t => t.Amount);
        var monthlyOutflow = Math.Abs(activeCycleTxs.Where(TransactionReportSemantics.IsReportableOutflow).Sum(t => t.Amount));
        var activeRecurringTotal = allRecurring
            .Where(payment => IsActiveInRange(payment, activeRange.start, activeRange.end))
            .Sum(payment => Math.Abs(MonthlyEquivalent(payment)));
        var growthPercentAchieved = selectedNetGrowth / (targetGrowth > 0 ? targetGrowth : 1m);
        var stabilityPercentReached = setting.TargetStabilityFund > 0m
            ? selectedRemStability / setting.TargetStabilityFund
            : 0m;

        var activeRecurringList = BuildActiveRecurringList(snapshot.RecurringOccurrences, snapshot.CycleRelevantTransactions);
        var selectedMonthRecurring = activeRecurringList
            .OrderBy(item => item.status == "Pending" || item.status == "PartiallyPaid" ? 0 : item.status == "Paid" ? 1 : 2)
            .ThenBy(item => item.dueDate)
            .ToList();

        var pendingRecurring = BuildPendingRecurringItems(
            snapshot.RecurringOccurrences,
            allRecurring.Select(payment => payment.Id).ToHashSet(StringComparer.Ordinal),
            snapshot.CycleRelevantTransactions);

        // Hoisted out of BuildTodayPlanInsights so the recovery block can hold its proposed draw
        // above the bills this cycle has already committed to, without computing the sum twice.
        var unpaidEssentials = pendingRecurring
            .Where(item => string.Equals(item.LedgerCategory, "Essentials", StringComparison.OrdinalIgnoreCase))
            .Sum(item => item.Amount);
        var unpaidRewards = pendingRecurring
            .Where(item => string.Equals(item.LedgerCategory, "Rewards", StringComparison.OrdinalIgnoreCase))
            .Sum(item => item.Amount);

        var todayPlanInsights = BuildTodayPlanInsights(
            activeCycleTxs,
            pendingRecurring,
            selectedRemEssentials,
            unpaidEssentials,
            activeRange.start,
            activeRange.end);

        // Null under summaryOnly: that path deliberately skips EnsureComputedThroughAsync, and the
        // reload obligation is only trustworthy once the cache is complete.
        var stabilityRecovery = summaryOnly
            ? null
            : await _stabilityRecoveryService.BuildAsync(
                setting,
                year,
                activeMonthIndex,
                activeCycleTxs,
                selectedBudgetStability,
                selectedRemStability,
                unpaidEssentials,
                cancellationToken,
                rewardsRecurringCommitted: unpaidRewards);

        var categoryLimitProgress = await BuildCategoryLimitProgressAsync(
            activeCycleTxs,
            pendingRecurring,
            activeYear,
            activeMonthIndex,
            cycleDay,
            activeRange.start,
            activeRange.end,
            cancellationToken);

        var pendingNotifications = summaryOnly
            ? new List<object>()
            : await _recurringPaymentAlertService.GetSubscriptionAlertsAsync(
                cycleDay,
                allRecurring,
                cancellationToken);

        var monthlyCategoryBreakdown = ReportMetricsCalculator.BuildBreakdown(activeCycleTxs);

        var cycleSummaryInsights = ReportResponseMapper.ObfuscateSummaryInsights(
            ReportMetricsCalculator.BuildSummaryInsights(activeCycleTxs, activeRangeStartDate, activeRangeEndExclusive, _financialClock.Today));

        var recurringAccountShortfalls = summaryOnly
            ? new List<object>()
            : RecurringAccountShortfallEvaluator.EvaluateShortfalls(
                snapshot.RecurringOccurrences,
                allRecurring,
                snapshot.LedgerAccounts,
                snapshot.LedgerAccountBalances.Current,
                _financialClock.Today,
                snapshot.CycleRelevantTransactions);

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
            stabilityRecovery,
            categoryLimitProgress,
            recurringAccountShortfalls,
            cycleSummaryInsights
        };

        stopwatch.Stop();
        Telemetry.DashboardLoadDuration.Record(stopwatch.ElapsedMilliseconds);

        return result;
    }

}
