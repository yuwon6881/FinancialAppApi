namespace FinancialAppApi.Services;

// Phase 2: the query planner. The single place where typed intents (plus the deterministic
// data-need flags computed from the resolved query text) become a concrete loading plan --
// the transaction data level and the set of derived metrics to compute. Combined intents
// produce the UNION of their requirements (metrics are additive; the transaction level takes
// the strongest requirement). There is no regex or database access here: pure intent -> plan.
public partial class AiAssistantService
{
    // Per-intent derived-metric mapping. Each intent that owns a specific server-side metric
    // declares it here, so adding an intent means adding one table row rather than editing a
    // switch. Intents whose data need is expressed through the cross-cutting flags
    // (cycle summary/comparison, wishlist forecast, budget targets) are handled below.
    private static readonly Dictionary<AiIntent, DerivedMetric> IntentMetricMap = new()
    {
        [AiIntent.LedgerActivityCount] = DerivedMetric.ActivityCount,
        [AiIntent.LedgerPurchaseFrequency] = DerivedMetric.PurchaseCadence,
        [AiIntent.LedgerMerchantSearch] = DerivedMetric.MerchantMatches,
        [AiIntent.LedgerAnomaly] = DerivedMetric.AnomalyDetection,
        [AiIntent.LedgerDuplicates] = DerivedMetric.DuplicateDetection,
        [AiIntent.RecurringUpcoming] = DerivedMetric.RecurringUpcoming
    };

    // Maps typed intents + data-need flags to the concrete loading plan (transaction level +
    // derived metrics). This is the single place intents become database decisions.
    private static AiQueryPlan BuildQueryPlan(
        IReadOnlyList<AiIntent> intents,
        bool needsTransactionDetail,
        bool needsCycleSummary,
        bool needsCycleComparison,
        bool needsWishlist,
        bool needsWishlistForecast,
        bool needsRecurring,
        bool needsBudgetTargets,
        bool needsCategoryLimits,
        bool needsCycleInsights,
        string? searchText,
        string? cycleHint,
        string queryText,
        IReadOnlyList<string>? transactionIds = null,
        int? wishlistItemId = null,
        bool needsLoans = false,
        bool needsLedgerAccounts = false)
    {
        var metrics = new List<DerivedMetric>();
        // Union: every intent contributes its owned metric.
        foreach (var intent in intents)
        {
            if (IntentMetricMap.TryGetValue(intent, out var metric)) metrics.Add(metric);
        }
        // Cross-cutting metrics driven by the aggregate data-need flags.
        if (needsCycleSummary) metrics.Add(DerivedMetric.CycleTotals);
        if (needsCycleComparison) metrics.Add(DerivedMetric.CycleComparison);
        if (needsWishlistForecast) metrics.Add(DerivedMetric.WishlistForecast);
        if (needsBudgetTargets) metrics.Add(DerivedMetric.AllocationPerformance);
        // A free-text entity is itself a request for matched transaction facts, regardless of
        // whether the wording was classified as merchant search, spending total, or list.
        if (!string.IsNullOrWhiteSpace(searchText) && !metrics.Contains(DerivedMetric.PurchaseCadence))
            metrics.Add(DerivedMetric.MerchantMatches);

        // Transaction level takes the strongest requirement across the combined intents:
        // matching rows (a count/merchant search needs exact matches) > bounded sample >
        // aggregate-only (a cycle question needs rows only to aggregate) > none.
        var transactionData = needsTransactionDetail
            ? (metrics.Contains(DerivedMetric.ActivityCount) || metrics.Contains(DerivedMetric.MerchantMatches) || metrics.Contains(DerivedMetric.PurchaseCadence)
                ? TransactionDataLevel.MatchingRows
                : TransactionDataLevel.BoundedSample)
            : needsCycleSummary
                ? (metrics.Contains(DerivedMetric.MerchantMatches) ? TransactionDataLevel.MatchingRows : TransactionDataLevel.AggregateOnly)
                : TransactionDataLevel.None;

        return new AiQueryPlan(
            intents,
            transactionData,
            metrics.Distinct().ToList(),
            searchText,
            cycleHint,
            queryText,
            transactionIds ?? [],
            wishlistItemId,
            needsTransactionDetail,
            needsCycleSummary,
            needsCycleComparison,
            needsWishlist,
            needsWishlistForecast,
            needsRecurring,
            needsBudgetTargets,
            needsCategoryLimits,
            needsCycleInsights,
            NeedsRewards: intents.Any(intent => intent is AiIntent.RewardsSummary or AiIntent.SavingsGoalList or
                AiIntent.SavingsGoalPacing or AiIntent.SavingsGoalScenario or AiIntent.SavingsGoalAdd or AiIntent.SavingsGoalEdit),
            NeedsInvestments: intents.Any(intent => intent is AiIntent.InvestmentSummary or AiIntent.InvestmentHolding or AiIntent.InvestmentAllocation),
            NeedsReport: intents.Contains(AiIntent.ReportReview),
            NeedsLoans: needsLoans || intents.Contains(AiIntent.LoanSummary),
            NeedsLedgerAccounts: needsLedgerAccounts || intents.Contains(AiIntent.LedgerAccount));
    }
}
