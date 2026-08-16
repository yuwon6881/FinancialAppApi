using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Accounts;
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
    string Currency,
    string? StabilityOverflowRedirect);

public class FinancialService
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
        string? paidDate);

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
                new LedgerAccountBalanceService(context),
                cycleBalanceService,
                _financialClock);
    }

    public async Task<object> GetWalletBalanceAsync(CancellationToken cancellationToken = default)
    {
        var setting = await GetOrCreateSettingAsync(cancellationToken);
        await _ledgerAccountService.ApplyDueInterestAsync(cancellationToken);

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
        // Load accounts once, then write due interest before the transaction snapshot so dashboard
        // totals and account rows observe the same balance without adding a bootstrap query.
        var ledgerAccounts = await _ledgerAccountService.GetAccountsAsync(cancellationToken);
        await _ledgerAccountService.ApplyDueInterestAsync(ledgerAccounts, cancellationToken);
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
            cancellationToken);

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

        var activeRecurringList = BuildActiveRecurringList(snapshot.RecurringOccurrences);
        var selectedMonthRecurring = activeRecurringList
            .OrderBy(item => item.status == "Pending" ? 0 : item.status == "Paid" ? 1 : 2)
            .ThenBy(item => item.dueDate)
            .ToList();

        var pendingRecurring = BuildPendingRecurringItems(snapshot.RecurringOccurrences);

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
            ReportMetricsCalculator.BuildSummaryInsights(activeCycleTxs, activeRangeStartDate, activeRangeEndExclusive));

        var recurringAccountShortfalls = summaryOnly
            ? new List<object>()
            : RecurringAccountShortfallEvaluator.EvaluateShortfalls(
                snapshot.RecurringOccurrences,
                allRecurring,
                snapshot.LedgerAccounts,
                snapshot.LedgerAccountBalances.Current,
                _financialClock.Today);

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
                .Where(t => t.LedgerCategory.ToLower() != "discarded")
                .GroupBy(t => t.Category)
                .Select(g => new ReportCategoryTotal(g.Key, g.Sum(t => t.Amount)))
                .ToListAsync(cancellationToken));
        }
        var yearlyCategoryBreakdown = yearlyGroups
            .GroupBy(g => string.IsNullOrWhiteSpace(g.Category) ? "Other" : g.Category.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new ReportBreakdownItem(g.Key, Math.Abs(g.Sum(x => x.Total))))
            .OrderByDescending(item => item.Amount)
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

    public async Task<string?> UpdateSettingsAsync(
        FinancialSettingsUpdate update,
        CancellationToken cancellationToken = default)
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

        var setting = await GetOrCreateSettingAsync(cancellationToken);
        var previousStabilityPlan = new FinancialSetting
        {
            TargetStabilityFund = setting.TargetStabilityFund,
            StabilityAlloc = setting.StabilityAlloc,
        };

        var targetStabilityFundChanged = targetStabilityFund != setting.TargetStabilityFund;
        var stabilityAllocChanged = update.StabilityAlloc != setting.StabilityAlloc;
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
        if (update.StabilityOverflowRedirect != null)
        {
            setting.StabilityOverflowRedirect = update.StabilityOverflowRedirect;
        }
        setting.Currency = update.Currency.Trim().ToUpperInvariant();

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            if (targetStabilityFundChanged || stabilityAllocChanged)
            {
                await _stabilityPlanRevisionService.AppendIfChangedAsync(
                    previousStabilityPlan,
                    setting,
                    DateTime.UtcNow,
                    cancellationToken);
            }
            if (cycleDayChanged)
            {
                await _cycleBalanceService.InvalidateAllAsync(cancellationToken);
            }
            else if (targetStabilityFundChanged || stabilityAllocChanged)
            {
                var currentCycle = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
                    _financialClock.Today,
                    setting.CycleDay);
                await _cycleBalanceService.InvalidateFromAsync(
                    currentCycle.year,
                    currentCycle.monthIndex,
                    cancellationToken);
            }
            await _context.SaveChangesAsync(cancellationToken);
            await dbTransaction.CommitAsync(cancellationToken);
        });
        return null;
    }

    public async Task UpdateDarkModeAsync(bool darkMode, CancellationToken cancellationToken = default)
    {
        var setting = await GetOrCreateSettingAsync(cancellationToken);
        setting.DarkMode = darkMode;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateHideSensitiveAsync(bool hideSensitive, CancellationToken cancellationToken = default)
    {
        var setting = await GetOrCreateSettingAsync(cancellationToken);
        setting.HideSensitive = hideSensitive;
        await _context.SaveChangesAsync(cancellationToken);
    }

    // Records which cycle the user has acknowledged an end-of-cycle summary for. Kept as a
    // tiny dedicated writer (like dark-mode/hide-sensitive) so acknowledging a summary never
    // races or overwrites a full settings edit. A null/blank key clears the marker.
    public async Task UpdateSummarySeenAsync(
        string? cycleKey,
        CancellationToken cancellationToken = default)
    {
        var setting = await GetOrCreateSettingAsync(cancellationToken);
        setting.LastSummaryCycleSeen = string.IsNullOrWhiteSpace(cycleKey) ? null : cycleKey.Trim();
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task SelectPeriodAsync(
        string selectedMonth,
        int selectedYear,
        CancellationToken cancellationToken = default)
    {
        var setting = await GetOrCreateSettingAsync(cancellationToken);
        setting.SelectedMonth = selectedMonth;
        setting.SelectedYear = selectedYear;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<FinancialSetting> GetOrCreateSettingAsync(CancellationToken cancellationToken)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
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
        List<RecurringPaymentOccurrence> occurrences)
    {
        return occurrences.Select(occurrence => new ActiveRecurringItem(
            occurrence.Id,
            occurrence.RecurringPaymentId,
            occurrence.Name,
            occurrence.ScheduledAmount.HasValue
                ? ObfuscationHelper.Obfuscate(Math.Abs(occurrence.ScheduledAmount.Value))
                : null,
            occurrence.Category ?? string.Empty,
            occurrence.LedgerCategory ?? string.Empty,
            occurrence.OccurrenceDate.ToString("yyyy-MM-dd"),
            occurrence.Status == RecurringOccurrenceStatus.Paid,
            occurrence.Status == RecurringOccurrenceStatus.Discarded,
            occurrence.Status,
            occurrence.PaidDate?.ToString("yyyy-MM-dd"))).ToList();
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

    private sealed record PendingRecurringItem(string Category, string LedgerCategory, decimal Amount);

    private static List<PendingRecurringItem> BuildPendingRecurringItems(
        List<RecurringPaymentOccurrence> occurrences) => occurrences
        .Where(occurrence => occurrence.Status == RecurringOccurrenceStatus.Pending
            && occurrence.ScheduledAmount.HasValue)
        .Select(occurrence => new PendingRecurringItem(
            occurrence.Category ?? string.Empty,
            occurrence.LedgerCategory ?? string.Empty,
            Math.Abs(occurrence.ScheduledAmount!.Value)))
        .ToList();

    private object BuildTodayPlanInsights(
        List<Transaction> activeCycleTxs,
        List<PendingRecurringItem> pendingRecurring,
        decimal essentialsRemaining,
        decimal unpaidEssentials,
        DateTime rangeStart,
        DateTime rangeEnd)
    {
        var nonRecurringEssentialsSpent = Math.Abs(activeCycleTxs
            .Where(transaction =>
                transaction.Amount < 0 &&
                TransactionReportSemantics.IsReportableOutflow(transaction) &&
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
        int cycleDay,
        DateTime rangeStart,
        DateTime rangeEnd,
        CancellationToken cancellationToken)
    {
        var cycleKey = $"{activeYear:D4}-{activeMonthIndex:D2}";
        var (currentCycleYear, currentCycleMonthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            _financialClock.Today,
            cycleDay);
        var currentCycleKey = $"{currentCycleYear:D4}-{currentCycleMonthIndex:D2}";
        var categoryTypes = (await _context.TransactionCategories
                .AsNoTracking()
                .Select(category => new { category.Name, category.Type })
                .ToListAsync(cancellationToken))
            .GroupBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Type, StringComparer.OrdinalIgnoreCase);
        var allGuideVersions = await _context.CategorySpendingGuides
            .AsNoTracking()
            .Where(guide => string.Compare(guide.EffectiveFromCycleKey, cycleKey) <= 0)
            .ToListAsync(cancellationToken);
        var effectiveGuides = allGuideVersions
            .GroupBy(guide => guide.CategoryName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(guide => guide.EffectiveFromCycleKey).First())
            .Where(guide => guide.LimitAmount.HasValue)
            // A current/future guide is valid only while its category accepts outflows.
            // Earlier cycles retain their historical guide even if the category later changes
            // flow type or is removed.
            .Where(guide => string.CompareOrdinal(guide.EffectiveFromCycleKey, currentCycleKey) < 0 ||
                (categoryTypes.TryGetValue(guide.CategoryName, out var type) &&
                 CategoryFlowType.AllowsSpendingGuide(type)))
            .OrderBy(guide => guide.CategoryName)
            .ToList();

        if (effectiveGuides.Count == 0) return [];

        var expenseTransactions = activeCycleTxs
            .Where(transaction =>
                transaction.Amount < 0 &&
                TransactionReportSemantics.IsReportableOutflow(transaction))
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

}
