using FinancialAppApi.Database;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    // The authoritative typed query plan. Carries the typed intents plus every data-loading
    // decision derived from them (what transaction level to pull, which derived metrics to
    // compute, and which optional blocks to include). The plan is the sole data-loading contract.
    internal sealed record AiQueryPlan(
        IReadOnlyList<AiIntent> Intents,
        TransactionDataLevel TransactionData,
        IReadOnlyList<DerivedMetric> Metrics,
        string? SearchText,
        string? CycleHint,
        string QueryText,
        IReadOnlyList<string> TransactionIds,
        int? WishlistItemId,
        bool NeedsTransactionDetail,
        bool NeedsCycleSummary,
        bool NeedsCycleComparison,
        bool NeedsWishlist,
        bool NeedsWishlistForecast,
        bool NeedsRecurring,
        bool NeedsBudgetTargets,
        bool NeedsCategoryLimits,
        bool NeedsCycleInsights,
        bool NeedsRewards = false,
        bool NeedsInvestments = false,
        bool NeedsReport = false,
        bool NeedsLoans = false,
        bool NeedsLedgerAccounts = false);

    private sealed record TargetCycleSelection(IReadOnlyList<CycleKey> Cycles, bool ExplicitlyRequested, bool AllHistory = false);
    internal sealed record AiTransactionRow(
        string Id,
        DateTime Timestamp,
        string Date,
        string Description,
        string Category,
        string LedgerCategory,
        decimal Amount,
        DateTime? PostedAt = null,
        string? RecurringPaymentId = null,
        string? AccountId = null,
        string? CounterAccountId = null);
    private sealed record AiTransactionDbRow(
        string Id,
        DateTime Date,
        DateTime PostedAt,
        string Description,
        string Category,
        string LedgerCategory,
        decimal Amount,
        string? RecurringPaymentId,
        string? AccountId,
        string? CounterAccountId);
    internal sealed record AiWishlistRow(int Id, string Name, decimal Price, string Priority, bool IsActive, bool IsPurchased, DateTime CreatedAt, DateTime? PurchasedAt = null);
    private sealed record TransactionDateRange(DateTime Start, DateTime End);

    private sealed record ContextSufficiency(bool Complete, bool Approximate, IReadOnlyList<AiDatasetKey> Missing);
    private sealed record AiContextBuildResult(
        AiContext Context,
        IReadOnlyList<CycleKey> TargetCycles,
        int CycleDay,
        int DefaultYear,
        ContextSufficiency Sufficiency,
        AiConversationState OutgoingState);

    private async Task<AiContextBuildResult> BuildContextAsync(
        AiIntentPlan intentPlan,
        bool forceSensitiveMode,
        CancellationToken cancellationToken)
    {
        var queryPlan = intentPlan.QueryPlan;
        var frame = await ResolveContextFrameAsync(intentPlan, cancellationToken);
        var setting = frame.Setting;
        var cycleDay = frame.CycleDay;
        var selectedMonth = frame.SelectedMonth;
        var selectedYear = frame.SelectedYear;
        var selectedMonthIndex = frame.SelectedMonthIndex;
        var categories = frame.Categories;
        var sensitiveMode = frame.SensitiveMode || forceSensitiveMode;
        var ledgerForecastRequest = frame.LedgerForecastRequest;
        var exactDate = frame.ExactDate;
        var targetSelection = frame.TargetSelection;

        var referenceDomains = await LoadReferenceDomainContextAsync(queryPlan, sensitiveMode, cancellationToken);
        var recurringContext = referenceDomains.RecurringContext;
        var recurringRows = referenceDomains.RecurringRows;
        var wishlistContext = referenceDomains.WishlistContext;
        var wishlistRows = referenceDomains.WishlistRows;
        var loanContext = await LoadLoanContextAsync(
            queryPlan,
            intentPlan.ConversationState.LastLoanId,
            sensitiveMode,
            cancellationToken);
        var transactionDomain = await LoadTransactionDomainContextAsync(
            intentPlan, targetSelection, cycleDay, exactDate, categories, cancellationToken);
        var allTransactions = transactionDomain.Transactions;
        var purchaseFrequency = await LoadPurchaseFrequencyAsync(queryPlan, targetSelection, cycleDay, cancellationToken);
        var ledgerAccountContext = await LoadLedgerAccountContextAsync(
            intentPlan, targetSelection, cycleDay, sensitiveMode, cancellationToken);
        var scopeTruncated = transactionDomain.ScopeTruncated;
        var exactMatchCount = transactionDomain.ExactMatchCount;
        var constraints = intentPlan.Constraints;
        var excludedCategories = transactionDomain.ExcludedCategories;
        var excludedLedgerCategories = transactionDomain.ExcludedLedgerCategories;
        var includedCategories = transactionDomain.IncludedCategories;
        var includedLedgerCategories = transactionDomain.IncludedLedgerCategories;
        var requestedTransactionType = transactionDomain.RequestedTransactionType;
        var appliesTransactionTypeFilter = transactionDomain.AppliesTransactionTypeFilter;
        var recentTransactions = BuildRecentTransactionsPayload(queryPlan, sensitiveMode, allTransactions);
        var recentTransactionIds = queryPlan.NeedsTransactionDetail
            ? allTransactions.Take(120).Select(t => t.Id).ToList()
            : [];
        var ledgerDomain = await BuildLedgerDomainContextAsync(
            intentPlan,
            setting,
            transactionDomain,
            targetSelection,
            wishlistRows,
            selectedYear,
            selectedMonthIndex,
            cycleDay,
            sensitiveMode,
            ledgerForecastRequest,
            cancellationToken);
        var wishlistForecast = ledgerDomain.WishlistForecast;
        var budgetTargets = BuildBudgetTargetsPayload(queryPlan, setting, sensitiveMode);
        var requestedCycles = BuildRequestedCyclesPayload(targetSelection, transactionDomain.CycleHasAnyRows, cycleDay);
        var turn = ResolveConversationTurn(
            intentPlan, queryPlan, targetSelection, exactDate, sensitiveMode,
            allTransactions, recurringRows, wishlistRows, ledgerAccountContext.SelectedAccountId);
        var outgoingState = queryPlan.NeedsLoans
            ? turn.OutgoingState with { LastLoanId = loanContext.SelectedLoanId ?? turn.OutgoingState.LastLoanId }
            : turn.OutgoingState;
        var turnFacets = turn.Facets;
        var turnExactDate = turn.ExactDate;
        var turnTransactionType = turn.TransactionType;
        var turnLedgerCategory = turn.LedgerCategory;
        var turnRecurringStatus = turn.RecurringStatus;
        var turnWishlistStatus = turn.WishlistStatus;
        var sufficiencyEvaluation = await EvaluateContextSufficiencyAsync(
            intentPlan, targetSelection, transactionDomain, wishlistRows, wishlistForecast,
            purchaseFrequency, sensitiveMode, cycleDay, cancellationToken);
        var intentNames = sufficiencyEvaluation.IntentNames;
        var recoveredOutflow = sufficiencyEvaluation.RecoveredOutflow;
        var perCycleRecoveredOutflow = sufficiencyEvaluation.PerCycleRecoveredOutflow;
        var sufficiencyResult = sufficiencyEvaluation.Result;
        var resolution = ToResolution(intentPlan);
        var datasets = await BuildContextDatasetsAsync(
            queryPlan, setting, intentPlan, allTransactions, targetSelection, selectedYear, selectedMonthIndex, cycleDay,
            sensitiveMode, exactMatchCount, perCycleRecoveredOutflow, purchaseFrequency, recurringRows,
            wishlistRows, ledgerDomain, ledgerAccountContext,
            cancellationToken);
        var cycleSummaries = datasets.CycleSummaries;
        var derivedMetrics = datasets.DerivedMetrics;
        var context = new AiContext(
            Currency: setting?.Currency ?? "USD",
            Today: _financialClock.Today.ToString("yyyy-MM-dd"),
            SensitiveMode: sensitiveMode,
            ActiveCycle: new
            {
                month = selectedMonth,
                year = selectedYear,
                label = CategoryAttributionService.GetCycleRange(selectedYear, selectedMonthIndex, cycleDay).label
            },
            Categories: categories,
            LedgerCategories: LedgerCategories,
            RequestedCycles: requestedCycles,
            DataScope: new
            {
                targetWasExplicit = targetSelection.ExplicitlyRequested,
                allHistory = targetSelection.AllHistory,
                aggregatesCoverAllTransactionsInRequestedCycles = targetSelection.Cycles.Count > 0 && !scopeTruncated,
                aggregatesTruncated = scopeTruncated,
                // Present only when the sample was truncated but the database returned the exact
                // headline outflow: use this figure, not the partial sample sum, and do not hedge.
                recoveredExactOutflow = recoveredOutflow,
                detailedTransactionsIncluded = queryPlan.NeedsTransactionDetail ? Math.Min(allTransactions.Count, 120) : 0,
                detailedTransactionsTotalInScope = allTransactions.Count
            },
            IntentNames: intentNames,
            IntentResolution: resolution == null
                ? null
                : new
                {
                    intents = resolution.Intents.Select(i => i.ToString()).ToList(),
                    confidence = resolution.Confidence,
                    usedClassifier = resolution.UsedClassifier,
                    ambiguities = resolution.Ambiguities
                },
            Sufficiency: new
            {
                canAnswer = sufficiencyResult.CanAnswer,
                isApproximate = sufficiencyResult.IsApproximate,
                missing = sufficiencyResult.Missing.Select(m => new { dataset = m.DatasetKey, reason = m.Reason }).ToList(),
                recoverable = sufficiencyResult.Recoverable.Select(m => new { dataset = m.DatasetKey, reason = m.Reason }).ToList()
            },
            QueryPlan: new
            {
                transactionData = queryPlan.TransactionData.ToString(),
                    metrics = queryPlan.Metrics.Select(metric => metric.ToString()).ToList(),
                    searchText = queryPlan.SearchText,
                    cycleHint = queryPlan.CycleHint,
                    operations = turnFacets,
                    exactDate = turnExactDate,
                    transactionType = turnTransactionType,
                    ledgerCategory = turnLedgerCategory,
                    recurringStatus = turnRecurringStatus,
                    wishlistStatus = turnWishlistStatus
                },
            ConversationState: outgoingState,
            Constraints: constraints == AiConstraints.None
                ? null
                : new
                {
                    preventNavigation = constraints.PreventNavigation,
                    excludeTransfers = constraints.ExcludeTransfers,
                    excludedCategories,
                    excludedLedgerCategories,
                    includedCategories,
                    includedLedgerCategories,
                    hypothetical = constraints.Hypothetical
                },
            DerivedMetrics: derivedMetrics,
            CycleSummaries: cycleSummaries,
            RecentTransactions: recentTransactions,
            RecentTransactionIds: recentTransactionIds,
            RecurringPayments: recurringContext,
            Loans: loanContext.Payload,
            LedgerAccounts: ledgerAccountContext.Payload,
            WishlistItems: wishlistContext,
            BudgetTargets: budgetTargets,
            WishlistForecast: wishlistForecast,
            CategoryLimits: await BuildCategoryLimitContextAsync(
                queryPlan, targetSelection.Cycles, cycleDay, allTransactions, sensitiveMode, cancellationToken),
            CycleInsights: BuildCycleInsightsContext(
                queryPlan, targetSelection.Cycles, cycleDay, allTransactions, sensitiveMode),
            Rewards: datasets.Rewards,
            Investments: datasets.Investments,
            ReportReview: datasets.ReportReview,
            RecurringAdvance: await BuildRecurringAdvanceContextAsync(
                queryPlan, recurringRows, cycleDay, cancellationToken),
            RecurringReminderStatus: await BuildRecurringReminderStatusContextAsync(
                queryPlan, recurringRows, cancellationToken),
            LedgerAccountSelectionIssue: ledgerAccountContext.SelectionIssue,
            LedgerAccountContext: ledgerAccountContext);
        var missing = sufficiencyResult.Missing.Select(m => m.DatasetKey).ToList();
        var sufficiency = new ContextSufficiency(
            Complete: sufficiencyResult.CanAnswer,
            // A truncated scope forces approximate wording UNLESS the exact figure was recovered
            // from the database (SQL SUM), in which case the headline number is precise.
            Approximate: sufficiencyResult.IsApproximate || (scopeTruncated && !recoveredOutflow.HasValue),
            Missing: missing);
        return new AiContextBuildResult(context, targetSelection.Cycles, cycleDay, selectedYear, sufficiency, outgoingState);
    }

    // The resolved "frame" of a context build: the user's settings plus the cycle selection and
    // exact-date narrowing every later section keys off.
    private sealed record AiContextFrame(
        Models.FinancialSetting? Setting,
        int CycleDay,
        string SelectedMonth,
        int SelectedYear,
        int SelectedMonthIndex,
        List<string> Categories,
        bool SensitiveMode,
        (string Ledger, decimal Target)? LedgerForecastRequest,
        DateOnly? ExactDate,
        TargetCycleSelection TargetSelection);

    private async Task<AiContextFrame> ResolveContextFrameAsync(
        AiIntentPlan intentPlan,
        CancellationToken cancellationToken)
    {
        var queryPlan = intentPlan.QueryPlan;
        var setting = await LoadFinancialSettingAsync(cancellationToken);
        var cycleDay = setting?.CycleDay ?? FinancialConstants.DefaultCycleDay;
        var selectedMonth = setting?.SelectedMonth ?? _financialClock.LocalNow.ToString("MMM");
        var selectedYear = setting?.SelectedYear ?? _financialClock.LocalNow.Year;
        var selectedMonthIndex = Array.IndexOf(FinancialConstants.MonthAbbreviations, selectedMonth) + 1;
        if (selectedMonthIndex <= 0) selectedMonthIndex = _financialClock.LocalNow.Month;

        var categories = (await _categoryService.GetCategoriesAsync())
            .Select(c => c.Name)
            .OrderBy(name => name)
            .ToList();

        var sensitiveMode = setting?.HideSensitive ?? true;
        // "how long until my Growth reaches 50000" -- a target-balance forecast for any ledger.
        var ledgerForecastRequest = sensitiveMode ? null : TryParseLedgerBalanceForecast(queryPlan.QueryText);
        DateOnly? exactDate = intentPlan.Entities?.Date;
        if (!exactDate.HasValue && TryExtractDate(queryPlan.QueryText, selectedYear, out var parsedExactDate, out _))
            exactDate = parsedExactDate;
        var targetSelection = ResolveTargetCycles(
            queryPlan.QueryText,
            selectedYear,
            selectedMonthIndex,
            queryPlan.NeedsCycleSummary,
            queryPlan.NeedsCycleComparison);
        if (exactDate.HasValue)
        {
            // A date can fall in the prior labelled cycle when cycleDay is not 1. Load the cycle
            // that actually contains it, then narrow rows to the exact calendar day below.
            targetSelection = new TargetCycleSelection([ResolveCycleContainingDate(exactDate.Value, cycleDay)], true);
        }
        if ((queryPlan.NeedsWishlistForecast || queryPlan.NeedsRewards || ledgerForecastRequest != null) && !targetSelection.ExplicitlyRequested)
        {
            // Forecasts always use the three completed cycles before today's active cycle. A user
            // viewing July must not make the assistant silently substitute July/June/May for the
            // current completed-cycle history shown by the Rewards page.
            var current = _financialClock.LocalNow;
            targetSelection = new TargetCycleSelection(
                Enumerable.Range(1, 3)
                    .Select(offset => AddMonths(current.Year, current.Month, -offset))
                    .Select(value => new CycleKey(value.Year, value.MonthIndex))
                    .ToList(),
                false);
        }

        return new AiContextFrame(
            setting,
            cycleDay,
            selectedMonth,
            selectedYear,
            selectedMonthIndex,
            categories,
            sensitiveMode,
            ledgerForecastRequest,
            exactDate,
            targetSelection);
    }

    private static object BuildRecentTransactionsPayload(
        AiQueryPlan queryPlan,
        bool sensitiveMode,
        IReadOnlyList<AiTransactionRow> allTransactions)
    {
        object recentTransactions;
        if (!queryPlan.NeedsTransactionDetail)
        {
            recentTransactions = Array.Empty<object>();
        }
        else if (sensitiveMode)
        {
            recentTransactions = allTransactions.Take(120).Select(t => new
            {
                t.Id,
                t.Date,
                t.Description,
                t.Category,
                t.LedgerCategory,
                txType = IsTransfer(t) ? "transfer" : t.Amount < 0 ? "outflow" : "inflow",
                accountId = queryPlan.NeedsLedgerAccounts ? null : t.AccountId,
                counterAccountId = queryPlan.NeedsLedgerAccounts ? null : t.CounterAccountId
            }).ToList();
        }
        else
        {
            recentTransactions = allTransactions.Take(120).Select(t => new
            {
                t.Id,
                t.Date,
                t.Description,
                t.Category,
                t.LedgerCategory,
                t.Amount,
                txType = IsTransfer(t) ? "transfer" : t.Amount < 0 ? "outflow" : "inflow",
                accountId = queryPlan.NeedsLedgerAccounts ? t.AccountId : null,
                counterAccountId = queryPlan.NeedsLedgerAccounts ? t.CounterAccountId : null
            }).ToList();
        }

        return recentTransactions;
    }

    // The user's allocation goals: fractions of income per ledger category plus the
    // stability-fund target. Actuals live in cycleSummaries.ledgerNet -- these targets are
    // what makes "how am I doing" / "where can I cut" answerable rather than guessed. Only
    // sent for analysis/coaching questions, and never in sensitiveMode (advice is
    // inherently amount-based, which sensitiveMode refuses anyway).
    private static object? BuildBudgetTargetsPayload(
        AiQueryPlan queryPlan,
        Models.FinancialSetting? setting,
        bool sensitiveMode)
    {
        return queryPlan.NeedsBudgetTargets && !sensitiveMode
            ? new
            {
                note = "Fractions of income allocated per ledger category. Compare against cycleSummaries.ledgerNet.",
                essentials = setting?.EssentialsAlloc ?? 0.50m,
                growth = setting?.GrowthAlloc ?? 0.25m,
                stability = setting?.StabilityAlloc ?? 0.15m,
                rewards = setting?.RewardsAlloc ?? 0.10m,
                targetStabilityFund = setting?.TargetStabilityFund ?? 0m,
                stabilityOverflowRedirect = setting?.StabilityOverflowRedirect ?? ""
            }
            : null;
    }

    private static object BuildRequestedCyclesPayload(
        TargetCycleSelection targetSelection,
        IReadOnlyDictionary<string, bool> cycleHasAnyRows,
        int cycleDay)
    {
        return targetSelection.Cycles
            .Select(c => new
            {
                month = FinancialConstants.MonthAbbreviations[c.MonthIndex - 1],
                year = c.Year,
                label = CategoryAttributionService.GetCycleRange(c.Year, c.MonthIndex, cycleDay).label,
                hasTransactions = cycleHasAnyRows.GetValueOrDefault(FormatCycleKey(c))
            })
            .ToList();
    }

    // This turn's resolved conversational frame: the outgoing state the client echoes back next
    // turn, plus the turn-scoped dimensions the context's queryPlan block reports.
    internal sealed record CycleKey(int Year, int MonthIndex);

    private sealed record AiContext(
        string Currency,
        string Today,
        bool SensitiveMode,
        object ActiveCycle,
        IReadOnlyList<string> Categories,
        IReadOnlyList<string> LedgerCategories,
        object RequestedCycles,
        object DataScope,
        IReadOnlyList<string> IntentNames,
        object? IntentResolution,
        object Sufficiency,
        object QueryPlan,
        AiConversationState? ConversationState,
        object? Constraints,
        object DerivedMetrics,
        object CycleSummaries,
        object RecentTransactions,
        // Just the ids of RecentTransactions. Action validation needs only these, so the
        // actionContext block carries this instead of a second copy of the full rows.
        IReadOnlyList<string> RecentTransactionIds,
        object RecurringPayments,
        object Loans,
        object WishlistItems,
        object? BudgetTargets,
        object? WishlistForecast,
        object? CategoryLimits,
        object? CycleInsights,
        object? RecurringAdvance,
        object? RecurringReminderStatus,
        object? Rewards = null,
        object? Investments = null,
        object? ReportReview = null,
        object? LedgerAccounts = null,
        string? LedgerAccountSelectionIssue = null,
        AiLedgerAccountContext? LedgerAccountContext = null);
}
