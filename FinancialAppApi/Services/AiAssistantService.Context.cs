using System.Globalization;
using System.Text.RegularExpressions;
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
        bool NeedsBudgetTargets);

    private sealed record TargetCycleSelection(IReadOnlyList<CycleKey> Cycles, bool ExplicitlyRequested);
    internal sealed record AiTransactionRow(
        string Id,
        DateTime Timestamp,
        string Date,
        string Description,
        string Category,
        string LedgerCategory,
        decimal Amount,
        DateTime? PostedAt = null);
    private sealed record AiTransactionDbRow(
        string Id,
        DateTime Date,
        DateTime PostedAt,
        string Description,
        string Category,
        string LedgerCategory,
        decimal Amount);
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
        var sensitiveMode = frame.SensitiveMode;
        var ledgerForecastRequest = frame.LedgerForecastRequest;
        var exactDate = frame.ExactDate;
        var targetSelection = frame.TargetSelection;

        var referenceDomains = await LoadReferenceDomainContextAsync(queryPlan, sensitiveMode, cancellationToken);
        var recurringContext = referenceDomains.RecurringContext;
        var recurringRows = referenceDomains.RecurringRows;
        var wishlistContext = referenceDomains.WishlistContext;
        var wishlistRows = referenceDomains.WishlistRows;

        var transactionDomain = await LoadTransactionDomainContextAsync(
            intentPlan, targetSelection, cycleDay, exactDate, categories, cancellationToken);
        var allTransactions = transactionDomain.Transactions;
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

        var requestedCycles = BuildRequestedCyclesPayload(targetSelection, allTransactions, cycleDay);

        var turn = ResolveConversationTurn(
            intentPlan, queryPlan, targetSelection, exactDate, sensitiveMode,
            allTransactions, recurringRows, wishlistRows);
        var outgoingState = turn.OutgoingState;
        var turnFacets = turn.Facets;
        var turnExactDate = turn.ExactDate;
        var turnTransactionType = turn.TransactionType;
        var turnLedgerCategory = turn.LedgerCategory;
        var turnRecurringStatus = turn.RecurringStatus;
        var turnWishlistStatus = turn.WishlistStatus;

        var sufficiencyEvaluation = await EvaluateContextSufficiencyAsync(
            intentPlan, targetSelection, transactionDomain, wishlistRows, wishlistForecast,
            sensitiveMode, cycleDay, cancellationToken);
        var intentNames = sufficiencyEvaluation.IntentNames;
        var recoveredOutflow = sufficiencyEvaluation.RecoveredOutflow;
        var perCycleRecoveredOutflow = sufficiencyEvaluation.PerCycleRecoveredOutflow;
        var sufficiencyResult = sufficiencyEvaluation.Result;
        var resolution = ToResolution(intentPlan);

        var datasets = await BuildContextDatasetsAsync(
            queryPlan, allTransactions, targetSelection, selectedYear, selectedMonthIndex, cycleDay,
            sensitiveMode, exactMatchCount, perCycleRecoveredOutflow, recurringRows, ledgerDomain,
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
            RecurringPayments: recurringContext,
            WishlistItems: wishlistContext,
            BudgetTargets: budgetTargets,
            WishlistForecast: wishlistForecast);
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
        var cycleDay = setting?.CycleDay ?? 28;
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
        if ((queryPlan.NeedsWishlistForecast || ledgerForecastRequest != null) && !targetSelection.ExplicitlyRequested)
        {
            // The active cycle plus the two before it -- matches the app's past-3 Rewards average
            // window (FinancialService.CalculatePastRewardsAverageFromTxs starts at the active
            // cycle), so the assistant's forecast lines up with the Wishlist page.
            targetSelection = new TargetCycleSelection(
                Enumerable.Range(0, 3)
                    .Select(offset => AddMonths(selectedYear, selectedMonthIndex, -offset))
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
                txType = t.Amount < 0 ? "outflow" : t.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase) ? "transfer" : "inflow"
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
                txType = t.Amount < 0 ? "outflow" : t.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase) ? "transfer" : "inflow"
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
        IReadOnlyList<AiTransactionRow> allTransactions,
        int cycleDay)
    {
        return targetSelection.Cycles
            .Select(c => new
            {
                month = FinancialConstants.MonthAbbreviations[c.MonthIndex - 1],
                year = c.Year,
                label = CategoryAttributionService.GetCycleRange(c.Year, c.MonthIndex, cycleDay).label,
                hasTransactions = allTransactions.Any(t => IsInCycle(t, c, cycleDay))
            })
            .ToList();
    }

    // This turn's resolved conversational frame: the outgoing state the client echoes back next
    // turn, plus the turn-scoped dimensions the context's queryPlan block reports.
    private sealed record ConversationTurn(
        AiConversationState OutgoingState,
        List<string> Facets,
        string? ExactDate,
        string? TransactionType,
        string? LedgerCategory,
        string? RecurringStatus,
        string? WishlistStatus);

    private static ConversationTurn ResolveConversationTurn(
        AiIntentPlan intentPlan,
        AiQueryPlan queryPlan,
        TargetCycleSelection targetSelection,
        DateOnly? exactDate,
        bool sensitiveMode,
        IReadOnlyList<AiTransactionRow> allTransactions,
        IReadOnlyList<AiRecurringRow> recurringRows,
        IReadOnlyList<AiWishlistRow> wishlistRows)
    {
        var constraints = intentPlan.Constraints;
        // Outgoing conversation state: structured references the client echoes back next turn.
        // Matched transaction ids are the rows the DB actually returned this turn (re-derived,
        // never the client's claimed ids), capped, and only when this was a matching-row query.
        var matchedIds = queryPlan.TransactionData == TransactionDataLevel.MatchingRows && !string.IsNullOrWhiteSpace(queryPlan.SearchText)
            ? allTransactions.Take(MaxStateMatchedIds).Select(t => t.Id).ToList()
            : null;
        var resolvedCycleLabel = targetSelection.Cycles.Count > 0
            ? $"{FinancialConstants.MonthAbbreviations[targetSelection.Cycles[0].MonthIndex - 1]} {targetSelection.Cycles[0].Year}"
            : null;
        int? resolvedWishlistItemId = wishlistRows.Count == 1
            ? wishlistRows[0].Id
            : wishlistRows.Count(w => w.IsActive && !w.IsPurchased) == 1
                ? wishlistRows.First(w => w.IsActive && !w.IsPurchased).Id
                : null;
        // Resolve this turn's frame dimensions so the next follow-up can inherit them. Each falls
        // back to the base (prior) value when not in play this turn, giving "sticky" context that a
        // later turn's explicit value overrides (and read-side family scoping stops an incompatible
        // turn from consuming it).
        var turnCycleKeys = targetSelection.Cycles.Count > 0
            ? targetSelection.Cycles.Select(FormatCycleKey).ToList()
            : null;
        var turnThreshold = TryParseAmountThreshold(queryPlan.QueryText) is { } parsedThreshold
            ? ToWireThreshold(parsedThreshold)
            : null;
        var turnExactDate = exactDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var turnTransactionType = DetectTransactionType(queryPlan.QueryText);
        var isRecurringTurn = intentPlan.Intents.Any(i => i is AiIntent.RecurringList or AiIntent.RecurringUpcoming or AiIntent.RecurringAdd or AiIntent.RecurringEdit);
        var isWishlistTurn = intentPlan.Intents.Any(i => i is AiIntent.WishlistList or AiIntent.WishlistForecast or AiIntent.WishlistAdd or AiIntent.WishlistEdit);
        var isTransactionalTurn = !isRecurringTurn && !isWishlistTurn;
        var turnFacets = DetectQueryFacets(queryPlan.QueryText).Where(KnownQueryFacets.Contains).Take(12).ToList();
        var turnIntentNames = intentPlan.Intents.Select(ToIntentName).ToList();
        var turnTopic = DetermineConversationTopic(queryPlan.QueryText, turnIntentNames, intentPlan.ConversationState)
            ?? intentPlan.ConversationState.LastTopic;
        var turnRecurringStatus = DetectRecurringStatus(queryPlan.QueryText);
        var turnWishlistStatus = DetectWishlistStatus(queryPlan.QueryText);
        var turnLedgerCategory = intentPlan.Entities?.LedgerCategory ?? DetectLedgerCategory(queryPlan.QueryText);
        var turnForecast = TryParseLedgerBalanceForecast(queryPlan.QueryText);
        var mentionedRecurringReference = FindMentionedEntityName(queryPlan.QueryText, recurringRows.Select(r => r.Name));
        var mentionedWishlistReference = FindMentionedEntityName(queryPlan.QueryText, wishlistRows.Select(w => w.Name));
        // A threshold-only question ("which transaction exceeded 100") has no searchText, so its
        // matching rows weren't captured above -- capture them here so a referential follow-up
        // ("which of those was the biggest?") can resolve against them.
        var thresholdMatchedIds = matchedIds == null && !sensitiveMode && ToInternalThreshold(turnThreshold) is { } activeThreshold
            ? allTransactions
                .Where(t => t.Amount < 0 && !IsTransfer(t) && MatchesThreshold(Math.Abs(t.Amount), activeThreshold))
                .Take(MaxStateMatchedIds).Select(t => t.Id).ToList()
            : null;
        if (thresholdMatchedIds is { Count: 0 }) thresholdMatchedIds = null;
        // Filter-lifting follow-ups drop the sticky exclusions instead of carrying them forever.
        // (queryPlan.QueryText contains the original message; expansion only appends, so these
        // phrases are still detectable and the appended canonical clauses never trip them.)
        var clearsFilters = WantsClearFilters(queryPlan.QueryText);
        var includesTransfers = WantsIncludeTransfers(queryPlan.QueryText);
        var clearsAmountFilter = WantsClearAmountFilter(queryPlan.QueryText);
        var clearsSearch = WantsClearSearch(queryPlan.QueryText);
        var baseState = intentPlan.ConversationState;
        var outgoingState = baseState with
        {
            LastSearchText = clearsSearch ? null : queryPlan.SearchText ?? baseState.LastSearchText,
            LastCycleHint = queryPlan.CycleHint ?? baseState.LastCycleHint,
            LastResolvedCycle = resolvedCycleLabel ?? baseState.LastResolvedCycle,
            LastMatchedTransactionIds = matchedIds ?? thresholdMatchedIds ?? baseState.LastMatchedTransactionIds,
            LastWishlistItemId = queryPlan.WishlistItemId.HasValue
                ? resolvedWishlistItemId
                : resolvedWishlistItemId ?? baseState.LastWishlistItemId,
            LastResolvedCycleKeys = turnCycleKeys ?? baseState.LastResolvedCycleKeys,
            LastAmountThreshold = clearsAmountFilter ? null : turnThreshold ?? baseState.LastAmountThreshold,
            LastExcludeTransfers = !includesTransfers && (constraints.ExcludeTransfers || baseState.LastExcludeTransfers),
            LastExcludedCategories = clearsFilters
                ? null
                : constraints.ExcludedCategories is { Count: > 0 }
                    ? constraints.ExcludedCategories
                    : baseState.LastExcludedCategories,
            LastIncludedCategories = clearsFilters
                ? null
                : constraints.IncludedCategories is { Count: > 0 }
                    ? constraints.IncludedCategories
                    : baseState.LastIncludedCategories,
            LastLedgerCategory = turnLedgerCategory ?? baseState.LastLedgerCategory,
            LastCategory = intentPlan.Entities?.Category ?? baseState.LastCategory,
            LastTransactionType = turnTransactionType ?? baseState.LastTransactionType,
            LastExactDate = isTransactionalTurn ? turnExactDate : baseState.LastExactDate,
            LastComparison = isTransactionalTurn ? queryPlan.NeedsCycleComparison : baseState.LastComparison,
            LastRecurringReference = isRecurringTurn
                ? mentionedRecurringReference ?? queryPlan.SearchText ?? baseState.LastRecurringReference
                : baseState.LastRecurringReference,
            LastWishlistReference = isWishlistTurn
                ? mentionedWishlistReference ?? baseState.LastWishlistReference
                : baseState.LastWishlistReference,
            LastTopic = turnTopic,
            LastQueryFacets = turnFacets.Count > 0 ? turnFacets : null,
            LastRecurringStatus = isRecurringTurn ? turnRecurringStatus : baseState.LastRecurringStatus,
            LastWishlistStatus = isWishlistTurn ? turnWishlistStatus : baseState.LastWishlistStatus,
            LastTargetAmount = isTransactionalTurn ? turnForecast?.Target : baseState.LastTargetAmount
        };

        return new ConversationTurn(
            outgoingState,
            turnFacets,
            turnExactDate,
            turnTransactionType,
            turnLedgerCategory,
            turnRecurringStatus,
            turnWishlistStatus);
    }

    private sealed record ContextSufficiencyEvaluation(
        SufficiencyResult Result,
        decimal? RecoveredOutflow,
        IReadOnlyDictionary<CycleKey, decimal>? PerCycleRecoveredOutflow,
        List<string> IntentNames);

    private async Task<ContextSufficiencyEvaluation> EvaluateContextSufficiencyAsync(
        AiIntentPlan intentPlan,
        TargetCycleSelection targetSelection,
        TransactionDomainContext transactionDomain,
        IReadOnlyList<AiWishlistRow> wishlistRows,
        object? wishlistForecast,
        bool sensitiveMode,
        int cycleDay,
        CancellationToken cancellationToken)
    {
        var queryPlan = intentPlan.QueryPlan;
        var allTransactions = transactionDomain.Transactions;
        var scopeTruncated = transactionDomain.ScopeTruncated;
        var exactMatchCount = transactionDomain.ExactMatchCount;

        // Phase 5: assemble explicit dataset statuses and run the sufficiency gate.
        var anyExplicitCycleEmpty = targetSelection.ExplicitlyRequested && targetSelection.Cycles.Count > 0 && allTransactions.Count == 0;
        var datasetStates = new Dictionary<AiDatasetKey, AiDatasetState>();
        if (queryPlan.NeedsTransactionDetail)
        {
            datasetStates[AiDatasetKey.TransactionDetails] = new AiDatasetState(
                scopeTruncated ? AiDatasetStatus.Truncated : anyExplicitCycleEmpty ? AiDatasetStatus.VerifiedEmpty : AiDatasetStatus.Available,
                TotalCount: allTransactions.Count, IncludedCount: Math.Min(allTransactions.Count, 120));
        }
        if (queryPlan.NeedsCycleSummary)
        {
            // First pass: no recovery has run yet, so a truncated sample never has an exact metric.
            datasetStates[AiDatasetKey.CycleSummaries] = new AiDatasetState(
                scopeTruncated ? AiDatasetStatus.Truncated : AiDatasetStatus.Available,
                HasExactMetric: !scopeTruncated);
        }
        if (queryPlan.Metrics.Contains(DerivedMetric.ActivityCount) || queryPlan.Metrics.Contains(DerivedMetric.MerchantMatches))
        {
            datasetStates[AiDatasetKey.TransactionMatches] = new AiDatasetState(
                scopeTruncated && !exactMatchCount.HasValue ? AiDatasetStatus.Truncated : AiDatasetStatus.Available,
                TotalCount: exactMatchCount, IncludedCount: allTransactions.Count, HasExactMetric: exactMatchCount.HasValue);
        }
        if (queryPlan.NeedsWishlist)
        {
            datasetStates[AiDatasetKey.Wishlist] = new AiDatasetState(wishlistRows.Count == 0 ? AiDatasetStatus.VerifiedEmpty : AiDatasetStatus.Available);
        }
        if (queryPlan.NeedsWishlistForecast)
        {
            datasetStates[AiDatasetKey.WishlistForecast] = sensitiveMode
                ? new AiDatasetState(AiDatasetStatus.Hidden, Reason: "hidden by sensitive mode")
                : new AiDatasetState(wishlistForecast == null ? AiDatasetStatus.VerifiedEmpty : AiDatasetStatus.Available);
        }
        if (queryPlan.NeedsRecurring)
        {
            datasetStates[AiDatasetKey.Recurring] = new AiDatasetState(AiDatasetStatus.Available);
        }
        if (queryPlan.NeedsBudgetTargets)
        {
            datasetStates[AiDatasetKey.BudgetTargets] = sensitiveMode
                ? new AiDatasetState(AiDatasetStatus.Hidden, Reason: "hidden by sensitive mode")
                : new AiDatasetState(AiDatasetStatus.Available);
        }
        var intentNames = intentPlan.Intents.Select(ToIntentName).ToList();

        // Phase 4 pipeline: validate what was loaded, recover only what the validator flagged as
        // deterministically recoverable, then validate again before generation ever runs. This
        // replaces the previous approach of recovering unconditionally whenever the scope was
        // truncated -- recovery now only fires for a dataset the first pass actually asked for.
        var firstPassResult = EvaluateSufficiency(intentNames, datasetStates);
        decimal? recoveredOutflow = null;
        IReadOnlyDictionary<CycleKey, decimal>? perCycleRecoveredOutflow = null;
        var needsCycleRecovery = firstPassResult.Recoverable.Any(r => r.DatasetKey == AiDatasetKey.CycleSummaries)
            && transactionDomain.CycleTotalRecoverable;
        if (needsCycleRecovery)
        {
            var searchFilter = queryPlan.TransactionData == TransactionDataLevel.MatchingRows ? queryPlan.SearchText : null;
            // Recovered per cycle (not one merged figure) so a multi-cycle comparison gets an
            // exact outflow for EACH requested cycle, not a single blended total across all of
            // them -- a blended total would be the wrong shape for "compare March vs April".
            perCycleRecoveredOutflow = await RecoverCycleOutflowByRangeAsync(
                targetSelection.Cycles, cycleDay, searchFilter, queryPlan.TransactionIds, cancellationToken);
            recoveredOutflow = perCycleRecoveredOutflow.Values.Sum();
            datasetStates[AiDatasetKey.CycleSummaries] = datasetStates[AiDatasetKey.CycleSummaries] with { HasExactMetric = true };
        }
        var sufficiencyResult = needsCycleRecovery
            ? EvaluateSufficiency(intentNames, datasetStates)
            : firstPassResult;

        return new ContextSufficiencyEvaluation(sufficiencyResult, recoveredOutflow, perCycleRecoveredOutflow, intentNames);
    }

    private sealed record ContextDatasets(List<object> CycleSummaries, object DerivedMetrics);

    private async Task<ContextDatasets> BuildContextDatasetsAsync(
        AiQueryPlan queryPlan,
        List<AiTransactionRow> allTransactions,
        TargetCycleSelection targetSelection,
        int selectedYear,
        int selectedMonthIndex,
        int cycleDay,
        bool sensitiveMode,
        int? exactMatchCount,
        IReadOnlyDictionary<CycleKey, decimal>? perCycleRecoveredOutflow,
        List<AiRecurringRow> recurringRows,
        LedgerDomainContext ledgerDomain,
        CancellationToken cancellationToken)
    {
        // Built after recovery so a per-cycle exact outflow (when recovered) replaces the
        // truncated sample sum in both the single-cycle summary and the multi-cycle comparison.
        var cycleSummaries = queryPlan.NeedsCycleSummary
            ? BuildCycleSummaries(allTransactions, targetSelection.Cycles, cycleDay, !sensitiveMode, perCycleRecoveredOutflow)
            : new List<object>();
        var balanceSnapshot = await BuildBalanceSnapshotAsync(
            queryPlan, allTransactions, selectedYear, selectedMonthIndex, cycleDay, sensitiveMode);
        var recurringInsights = await BuildRecurringDomainInsightsAsync(
            queryPlan, recurringRows, targetSelection, selectedYear, selectedMonthIndex, cycleDay, sensitiveMode, cancellationToken);
        var recurringBillStatus = recurringInsights.BillStatus;
        var recurringUpcoming = recurringInsights.Upcoming;
        var recurringCostSummary = recurringInsights.CostSummary;

        var extraMetrics = new Dictionary<string, object?>();
        if (recurringUpcoming != null) extraMetrics["upcomingBills"] = recurringUpcoming;
        if (ledgerDomain.StabilityProgress != null) extraMetrics["stabilityProgress"] = ledgerDomain.StabilityProgress;
        if (ledgerDomain.AffordableWishlistCount != null) extraMetrics["affordableWishlistCount"] = ledgerDomain.AffordableWishlistCount;
        if (ledgerDomain.LedgerBalanceForecast != null) extraMetrics["ledgerBalanceForecast"] = ledgerDomain.LedgerBalanceForecast;
        if (recurringCostSummary != null) extraMetrics["recurringCostSummary"] = recurringCostSummary;

        var derivedMetrics = BuildDerivedMetrics(queryPlan, allTransactions, targetSelection.Cycles, cycleDay, sensitiveMode, exactMatchCount, perCycleRecoveredOutflow, balanceSnapshot, recurringBillStatus, extraMetrics);

        return new ContextDatasets(cycleSummaries, derivedMetrics);
    }

    private static object BuildDerivedMetrics(
        AiQueryPlan queryPlan,
        IReadOnlyList<AiTransactionRow> transactions,
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        bool sensitiveMode,
        int? exactMatchCount,
        IReadOnlyDictionary<CycleKey, decimal>? perCycleRecoveredOutflow,
        object? balanceSnapshot = null,
        object? recurringBillStatus = null,
        IReadOnlyDictionary<string, object?>? extraMetrics = null)
    {
        var metrics = new Dictionary<string, object?>();
        if (recurringBillStatus != null) metrics["recurringBillStatus"] = recurringBillStatus;
        if (extraMetrics != null)
        {
            foreach (var kv in extraMetrics) metrics[kv.Key] = kv.Value;
        }
        if (queryPlan.Metrics.Contains(DerivedMetric.ActivityCount) || queryPlan.Metrics.Contains(DerivedMetric.MerchantMatches))
        {
            metrics["transactionMatches"] = new
            {
                query = queryPlan.SearchText,
                count = exactMatchCount ?? transactions.Count,
                totalOutflow = sensitiveMode ? (decimal?)null : Math.Abs(transactions.Where(t => t.Amount < 0 && !IsTransfer(t)).Sum(t => t.Amount)),
                complete = queryPlan.TransactionData == TransactionDataLevel.MatchingRows && (exactMatchCount.HasValue || transactions.Count < MaxTransactionsPerRange),
                    rows = sensitiveMode
                        ? transactions.Take(120).Select(t => (object)new { t.Id, t.Date, t.Description, t.Category }).ToList()
                        : transactions.Take(120).Select(t => (object)new { t.Id, t.Date, t.Description, t.Category, t.Amount }).ToList()
            };
        }
        if (balanceSnapshot != null) metrics["balanceSnapshot"] = balanceSnapshot;
        // Amount-threshold filter ("which transaction exceeded 250", "purchases over 100",
        // "anything between 50 and 200"). Amount-based, so suppressed in sensitive mode like the
        // other magnitude metrics.
        var threshold = TryParseAmountThreshold(queryPlan.QueryText);
        if (!sensitiveMode && threshold != null)
        {
            metrics["thresholdMatches"] = BuildThresholdMatches(
                transactions, threshold, sampleWasComplete: transactions.Count < MaxTransactionsPerRange);
        }
        // Superlative single-record ranking ("biggest transaction", "largest spending", "smallest
        // purchase", "biggest deposit"). Exact over the loaded rows, transfers excluded, spending
        // and income ranked separately -- so the model never has to eyeball a mixed sample (which
        // let a transfer or inflow surface as the "biggest spending"). Amount-based, so suppressed
        // in sensitive mode like the other magnitude metrics.
        if (!sensitiveMode
            && (queryPlan.NeedsTransactionDetail || queryPlan.NeedsCycleSummary)
            && WantsTopTransactions(queryPlan.QueryText))
        {
            metrics["topTransactions"] = BuildTopTransactions(transactions, queryPlan.QueryText);
        }
        if (Regex.IsMatch(queryPlan.QueryText, @"\b(which|what) day\b.*\b(most|highest|largest)\b|\bmost\b.*\b(day|daily)\b", RegexOptions.IgnoreCase))
        {
            var daily = transactions.Where(t => !IsTransfer(t))
                .GroupBy(t => t.Date)
                .Select(g => new
                {
                    date = g.Key,
                    inflow = g.Where(t => t.Amount > 0).Sum(t => t.Amount),
                    outflow = Math.Abs(g.Where(t => t.Amount < 0).Sum(t => t.Amount))
                }).ToList();
            metrics["dailyExtremes"] = sensitiveMode || daily.Count == 0 ? null : new
            {
                highestInflowDay = daily.OrderByDescending(d => d.inflow).First(),
                highestOutflowDay = daily.OrderByDescending(d => d.outflow).First()
            };
        }
        if (queryPlan.TransactionIds.Count > 0)
        {
            var referenced = transactions
                .Where(t => queryPlan.TransactionIds.Contains(t.Id, StringComparer.Ordinal))
                .ToList();
            var outflows = referenced.Where(t => t.Amount < 0 && !IsTransfer(t)).ToList();
            var highest = outflows.OrderByDescending(t => Math.Abs(t.Amount)).FirstOrDefault();
            metrics["referencedTransactions"] = new
            {
                ids = referenced.Select(t => t.Id).ToList(),
                count = referenced.Count,
                totalCost = Math.Abs(outflows.Sum(t => t.Amount)),
                highest = highest == null ? null : new
                {
                    highest.Id,
                    highest.Description,
                    highest.Date,
                    amount = sensitiveMode ? (decimal?)null : Math.Abs(highest.Amount)
                },
                complete = referenced.Count == queryPlan.TransactionIds.Count
            };
        }
        if (queryPlan.Metrics.Contains(DerivedMetric.CycleComparison))
        {
            metrics["cycleComparison"] = BuildCycleComparison(transactions, cycles, cycleDay, perCycleRecoveredOutflow);
        }
        if (queryPlan.Metrics.Contains(DerivedMetric.AnomalyDetection))
        {
            metrics["anomalies"] = DetectAnomalies(transactions)
                .Select(a => sensitiveMode
                    ? (object)new { a.Id, a.Description, a.Category, amount = (decimal?)null, a.Score, a.Reason }
                    : new { a.Id, a.Description, a.Category, amount = (decimal?)a.Amount, a.Score, a.Reason })
                .ToList();
        }
        if (queryPlan.Metrics.Contains(DerivedMetric.DuplicateDetection))
        {
            metrics["duplicateCandidates"] = DetectDuplicates(transactions)
                .Select(d => sensitiveMode
                    ? (object)new { d.Ids, d.Description, d.DaysApart, d.Confidence, d.Reasons, amount = (decimal?)null }
                    : new { d.Ids, d.Description, d.DaysApart, d.Confidence, d.Reasons, amount = (decimal?)d.Amount })
                .ToList();
        }
        return metrics;
    }

    private static object BuildCycleComparison(
        IReadOnlyList<AiTransactionRow> transactions,
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        IReadOnlyDictionary<CycleKey, decimal>? perCycleRecoveredOutflow)
    {
        return cycles.Select(cycle =>
        {
            var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
            var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start));
            var end = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end));
            var txs = transactions.Where(t => t.Timestamp >= start && t.Timestamp < end && !IsTransfer(t)).ToList();
            // Prefer the recovered exact SUM for this specific cycle when the sample was
            // truncated; otherwise the in-memory sample sum is already complete.
            var recovered = perCycleRecoveredOutflow != null && perCycleRecoveredOutflow.ContainsKey(cycle);
            var outflow = recovered ? perCycleRecoveredOutflow![cycle] : Math.Abs(txs.Where(t => t.Amount < 0).Sum(t => t.Amount));
            return new
            {
                month = FinancialConstants.MonthAbbreviations[cycle.MonthIndex - 1],
                year = cycle.Year,
                inflow = txs.Where(t => t.Amount > 0).Sum(t => t.Amount),
                outflow,
                net = txs.Where(t => t.Amount > 0).Sum(t => t.Amount) - outflow,
                exactOutflow = recovered
            };
        }).ToList();
    }

    private static List<object> BuildCycleSummaries(
        IReadOnlyList<AiTransactionRow> transactions,
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        bool includeAmounts,
        IReadOnlyDictionary<CycleKey, decimal>? perCycleRecoveredOutflow)
    {
        var summaries = new List<object>();
        foreach (var cycle in cycles)
        {
            var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
            var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start));
            var end = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end));
            var txs = transactions
                .Where(t => t.Timestamp >= start && t.Timestamp < end)
                .ToList();
            var nonTransferTxs = txs.Where(t => !IsTransfer(t)).ToList();

            if (!includeAmounts)
            {
                summaries.Add(new
                {
                    month = FinancialConstants.MonthAbbreviations[cycle.MonthIndex - 1],
                    year = cycle.Year,
                    label = range.label,
                    transactionCount = txs.Count,
                    categoryCounts = txs
                        .GroupBy(t => t.Category)
                        .Select(g => new { category = g.Key, count = g.Count() })
                        .OrderByDescending(x => x.count)
                        .Take(8)
                        .ToList(),
                    ledgerCounts = txs
                        .GroupBy(t => t.LedgerCategory)
                        .Select(g => new { ledgerCategory = g.Key, count = g.Count() })
                        .OrderByDescending(x => x.count)
                        .Take(8)
                        .ToList(),
                    recentTransactions = txs
                        .OrderByDescending(t => t.Timestamp)
                        .ThenByDescending(t => t.PostedAt ?? t.Timestamp)
                        .Take(12)
                        .Select(t => new
                        {
                            t.Id,
                            t.Date,
                            t.Description,
                            t.Category,
                            t.LedgerCategory,
                            txType = t.Amount < 0 ? "outflow" : t.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase) ? "transfer" : "inflow"
                        })
                        .ToList()
                });
                continue;
            }

            var categorySpend = nonTransferTxs
                .Where(t => t.Amount < 0)
                .GroupBy(t => t.Category)
                .Select(g => new { category = g.Key, outflow = Math.Abs(g.Sum(t => t.Amount)) })
                .OrderByDescending(x => x.outflow)
                .Take(8)
                .ToList();

            // Ledger net must be summed over ALL cycle transactions (transfers included), exactly
            // like the authoritative FinancialService/CycleBalanceService: GetCategoryAmount is
            // what routes a Transfer:Source->Target row into the -source/+target ledgers. Summing
            // over nonTransferTxs instead dropped every transfer, so ledgers funded by transfers
            // (typically Growth and Stability) wrongly showed a net of 0 / understated balances.
            var ledgerNet = LedgerCategories
                .Where(c => c != "Income")
                .Select(c => new
                {
                    ledgerCategory = c,
                        net = txs.Sum(t => CategoryAttributionService.GetCategoryAmount(new Models.Transaction
                    {
                        Amount = t.Amount,
                        LedgerCategory = t.LedgerCategory
                    }, c))
                })
                .ToList();

            var recoveredExact = perCycleRecoveredOutflow != null && perCycleRecoveredOutflow.ContainsKey(cycle);
            var income = nonTransferTxs.Where(t => t.Amount > 0).Sum(t => t.Amount);
            var outflow = recoveredExact ? perCycleRecoveredOutflow![cycle] : Math.Abs(nonTransferTxs.Where(t => t.Amount < 0).Sum(t => t.Amount));
            summaries.Add(new
            {
                month = FinancialConstants.MonthAbbreviations[cycle.MonthIndex - 1],
                year = cycle.Year,
                label = range.label,
                transactionCount = txs.Count,
                income,
                outflow,
                netChange = income - outflow,
                // True when this cycle's outflow/netChange came from an exact database SUM
                // rather than the (possibly truncated) in-memory sample.
                exactOutflow = recoveredExact,
                categorySpend,
                ledgerNet,
                largestTransactions = txs
                    .OrderByDescending(t => Math.Abs(t.Amount))
                    .Take(10)
                    .Select(t => new
                    {
                        t.Id,
                        t.Date,
                        t.Description,
                        t.Category,
                        t.LedgerCategory,
                        t.Amount,
                        txType = t.Amount < 0 ? "outflow" : t.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase) ? "transfer" : "inflow"
                    })
                    .ToList()
            });
        }
        return summaries;
    }

    private static bool IsTransfer(AiTransactionRow transaction) =>
        transaction.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase) ||
        transaction.Category.Equals("Transfer", StringComparison.OrdinalIgnoreCase);

    private static bool IsInCycle(AiTransactionRow transaction, CycleKey cycle, int cycleDay)
    {
        var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
        var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start));
        var end = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end));
        return transaction.Timestamp >= start && transaction.Timestamp < end;
    }

    private static IReadOnlyList<TransactionDateRange> MergeCycleRanges(IReadOnlyList<CycleKey> cycles, int cycleDay)
    {
        var ranges = cycles
            .Distinct()
            .Select(cycle => CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay))
            .Select(range => new TransactionDateRange(
                TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start)),
                TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end))))
            .OrderBy(range => range.Start)
            .ToList();
        if (ranges.Count <= 1) return ranges;

        var merged = new List<TransactionDateRange> { ranges[0] };
        foreach (var range in ranges.Skip(1))
        {
            var previous = merged[^1];
            if (range.Start <= previous.End)
            {
                merged[^1] = previous with { End = range.End > previous.End ? range.End : previous.End };
            }
            else
            {
                merged.Add(range);
            }
        }
        return merged;
    }

    private static TargetCycleSelection ResolveTargetCycles(
        string queryText,
        int selectedYear,
        int selectedMonthIndex,
        bool needsCycleSummary,
        bool needsComparison)
    {
        var explicitCycles = Regex.Matches(
                queryText,
                $@"\b(?<month>{MonthNamePattern})\s+(?<year>(?:19|20)\d{{2}})\b",
                RegexOptions.IgnoreCase)
            .Select(match => new CycleKey(
                int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture),
                GetMonthNumber(match.Groups["month"].Value)))
            .Distinct()
            .Take(12)
            .ToList();
        explicitCycles.AddRange(Regex.Matches(queryText, @"\b(?<year>(?:19|20)\d{2})-(?<month>0?[1-9]|1[0-2])\b")
            .Select(match => new CycleKey(
                int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture))));
        explicitCycles = explicitCycles.Distinct().Take(24).ToList();
        if (explicitCycles.Count > 0)
        {
            if (explicitCycles.Count == 2 &&
                Regex.IsMatch(queryText, @"\b(between|through|until|from)\b|\bto\b", RegexOptions.IgnoreCase))
            {
                explicitCycles = ExpandCycleRange(explicitCycles[0], explicitCycles[1], 24);
            }
            return new TargetCycleSelection(explicitCycles, true);
        }

        // "all cycles" / "every month" / "across all cycles" / "all-time": a search or
        // superlative that spans the user's whole history. Without this the query fell through
        // to the single active-cycle default, so "show badminton for all cycle" or "the most I
        // deposited into stability across all cycles" only ever looked at the current cycle.
        // Bounded to the trailing 24 cycles (they merge into one contiguous range) to stay
        // within the per-range row cap.
        if (Regex.IsMatch(queryText, @"\b(all|every|each)\s+(cycles?|months?)\b|\b(across|over|through(?:out)?|in)\s+all\b|\ball[- ]?time\b", RegexOptions.IgnoreCase))
        {
            return new TargetCycleSelection(
                Enumerable.Range(0, 24)
                    .Select(offset => AddMonths(selectedYear, selectedMonthIndex, -offset))
                    .Select(value => new CycleKey(value.Year, value.MonthIndex))
                    .ToList(),
                true);
        }

        var wholeYear = Regex.Match(
            queryText,
            @"\b(?:in|during|for|year)\s+(?<year>(?:19|20)\d{2})\b",
            RegexOptions.IgnoreCase);
        if (wholeYear.Success)
        {
            var year = int.Parse(wholeYear.Groups["year"].Value, CultureInfo.InvariantCulture);
            return new TargetCycleSelection(
                Enumerable.Range(1, 12).Select(month => new CycleKey(year, month)).ToList(),
                true);
        }

        if (Regex.IsMatch(queryText, @"\b(last|previous|prior)\s+year\b", RegexOptions.IgnoreCase))
        {
            return new TargetCycleSelection(
                Enumerable.Range(1, 12).Select(month => new CycleKey(selectedYear - 1, month)).ToList(),
                true);
        }
        if (Regex.IsMatch(queryText, @"\b(this|current)\s+year\b", RegexOptions.IgnoreCase))
        {
            return new TargetCycleSelection(
                Enumerable.Range(1, 12).Select(month => new CycleKey(selectedYear, month)).ToList(),
                true);
        }

        var quarter = Regex.Match(queryText,
            @"\b(?:q(?<number>[1-4])|(?<word>first|second|third|fourth) quarter)(?:\s+(?<year>(?:19|20)\d{2}))?\b",
            RegexOptions.IgnoreCase);
        if (quarter.Success)
        {
            var number = quarter.Groups["number"].Success
                ? int.Parse(quarter.Groups["number"].Value, CultureInfo.InvariantCulture)
                : quarter.Groups["word"].Value.ToLowerInvariant() switch
                {
                    "first" => 1, "second" => 2, "third" => 3, _ => 4
                };
            var year = quarter.Groups["year"].Success
                ? int.Parse(quarter.Groups["year"].Value, CultureInfo.InvariantCulture)
                : selectedYear;
            return new TargetCycleSelection(
                Enumerable.Range((number - 1) * 3 + 1, 3).Select(month => new CycleKey(year, month)).ToList(),
                true);
        }

        var relativeQuarter = Regex.Match(queryText, @"\b(?<which>this|current|last|previous|prior|next) quarter\b", RegexOptions.IgnoreCase);
        if (relativeQuarter.Success)
        {
            var activeOrdinal = selectedYear * 12 + selectedMonthIndex - 1;
            var activeQuarterStart = activeOrdinal - ((selectedMonthIndex - 1) % 3);
            var shift = relativeQuarter.Groups["which"].Value.ToLowerInvariant() switch
            {
                "last" or "previous" or "prior" => -3,
                "next" => 3,
                _ => 0
            };
            return new TargetCycleSelection(
                Enumerable.Range(activeQuarterStart + shift, 3)
                    .Select(ordinal => new CycleKey(ordinal / 12, ordinal % 12 + 1)).ToList(),
                true);
        }

        var cyclesAgo = Regex.Match(queryText, @"\b(?<count>\d{1,2})\s+(?:cycles?|months?)\s+ago\b", RegexOptions.IgnoreCase);
        if (cyclesAgo.Success)
        {
            var offset = -Math.Clamp(int.Parse(cyclesAgo.Groups["count"].Value, CultureInfo.InvariantCulture), 1, 120);
            var target = AddMonths(selectedYear, selectedMonthIndex, offset);
            return new TargetCycleSelection([new CycleKey(target.Year, target.MonthIndex)], true);
        }
        if (Regex.IsMatch(queryText, @"\b(?:cycle|month)\s+before\s+last\b", RegexOptions.IgnoreCase))
        {
            var target = AddMonths(selectedYear, selectedMonthIndex, -2);
            return new TargetCycleSelection([new CycleKey(target.Year, target.MonthIndex)], true);
        }

        var relativeCount = Regex.Match(
            queryText,
            @"\b(?:last|past|previous|prior)\s+(?<count>\d{1,2}|few)\s+(?:cycles?|months?)\b",
            RegexOptions.IgnoreCase);
        if (relativeCount.Success)
        {
            var count = relativeCount.Groups["count"].Value.Equals("few", StringComparison.OrdinalIgnoreCase)
                ? 3
                : Math.Clamp(int.Parse(relativeCount.Groups["count"].Value, CultureInfo.InvariantCulture), 1, 12);
            return new TargetCycleSelection(
                Enumerable.Range(1, count)
                    .Select(offset => AddMonths(selectedYear, selectedMonthIndex, -offset))
                    .Select(value => new CycleKey(value.Year, value.MonthIndex))
                    .ToList(),
                true);
        }

        var cycles = new List<CycleKey>();
        if (Regex.IsMatch(queryText, @"\b(this|current)\s+(cycle|month)\b", RegexOptions.IgnoreCase))
        {
            cycles.Add(new CycleKey(selectedYear, selectedMonthIndex));
        }
        if (Regex.IsMatch(queryText, @"\b(previous|prior|last)\s+(cycle|month)\b", RegexOptions.IgnoreCase))
        {
            var previous = AddMonths(selectedYear, selectedMonthIndex, -1);
            cycles.Add(new CycleKey(previous.Year, previous.MonthIndex));
            if (needsComparison) cycles.Add(new CycleKey(selectedYear, selectedMonthIndex));
        }
        if (Regex.IsMatch(queryText, @"\b(next|following)\s+(cycle|month)\b", RegexOptions.IgnoreCase))
        {
            var next = AddMonths(selectedYear, selectedMonthIndex, 1);
            cycles.Add(new CycleKey(next.Year, next.MonthIndex));
            if (needsComparison) cycles.Add(new CycleKey(selectedYear, selectedMonthIndex));
        }
        if (cycles.Count > 0) return new TargetCycleSelection(cycles.Distinct().ToList(), true);

        var namedMonth = Regex.Match(
            queryText,
            $@"(?:\b(?:cycle|month|in|for|about|during|and|vs\.?|versus|with|against)\s+|^\s*(?:(?:and|also|then|now|what about|how about|instead|just)\s+)?)(?<month>{MonthNamePattern})\b",
            RegexOptions.IgnoreCase);
        if (namedMonth.Success)
        {
            return new TargetCycleSelection(
                [new CycleKey(selectedYear, GetMonthNumber(namedMonth.Groups["month"].Value))],
                true);
        }

        if (!needsCycleSummary) return new TargetCycleSelection([], false);
        if (!needsComparison)
        {
            return new TargetCycleSelection([new CycleKey(selectedYear, selectedMonthIndex)], false);
        }

        return new TargetCycleSelection(
            Enumerable.Range(-6, 7)
                .Select(offset => AddMonths(selectedYear, selectedMonthIndex, offset))
                .Select(value => new CycleKey(value.Year, value.MonthIndex))
                .ToList(),
            false);
    }

    private static List<CycleKey> ExpandCycleRange(CycleKey first, CycleKey second, int maximum)
    {
        var firstOrdinal = first.Year * 12 + first.MonthIndex - 1;
        var secondOrdinal = second.Year * 12 + second.MonthIndex - 1;
        var start = Math.Min(firstOrdinal, secondOrdinal);
        var end = Math.Max(firstOrdinal, secondOrdinal);
        return Enumerable.Range(start, Math.Min(end - start + 1, maximum))
            .Select(ordinal => new CycleKey(ordinal / 12, ordinal % 12 + 1))
            .ToList();
    }

    private static (int Year, int MonthIndex) AddMonths(int year, int monthIndex, int offset)
    {
        var zeroBased = (monthIndex - 1) + offset;
        year += (int)Math.Floor(zeroBased / 12.0);
        var month = ((zeroBased % 12) + 12) % 12 + 1;
        return (year, month);
    }

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
        object RecurringPayments,
        object WishlistItems,
        object? BudgetTargets,
        object? WishlistForecast);
}
