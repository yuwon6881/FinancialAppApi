namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
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
        AiPurchaseFrequencyMetric? purchaseFrequency,
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
        if (queryPlan.Metrics.Contains(DerivedMetric.PurchaseCadence))
        {
            datasetStates[AiDatasetKey.PurchaseFrequency] = new AiDatasetState(
                purchaseFrequency == null ? AiDatasetStatus.Unavailable
                    : purchaseFrequency.TransactionCount == 0 ? AiDatasetStatus.VerifiedEmpty : AiDatasetStatus.Available,
                TotalCount: purchaseFrequency?.TransactionCount,
                IncludedCount: purchaseFrequency?.TransactionCount ?? 0, HasExactMetric: purchaseFrequency != null);
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
        if (queryPlan.NeedsRewards)
        {
            datasetStates[AiDatasetKey.Rewards] = sensitiveMode
                ? new AiDatasetState(AiDatasetStatus.Hidden, Reason: "hidden by sensitive mode")
                : new AiDatasetState(AiDatasetStatus.Available);
        }
        if (queryPlan.NeedsInvestments)
        {
            datasetStates[AiDatasetKey.Investments] = sensitiveMode
                ? new AiDatasetState(AiDatasetStatus.Hidden, Reason: "hidden by sensitive mode")
                : _investmentPortfolioService == null
                    ? new AiDatasetState(AiDatasetStatus.Unavailable, Reason: "investment portfolio is unavailable")
                    : new AiDatasetState(AiDatasetStatus.Available);
        }
        if (queryPlan.NeedsReport)
        {
            datasetStates[AiDatasetKey.ReportReview] = sensitiveMode
                ? new AiDatasetState(AiDatasetStatus.Hidden, Reason: "hidden by sensitive mode")
                : new AiDatasetState(AiDatasetStatus.Available);
        }
        if (queryPlan.NeedsRecurring)
        {
            datasetStates[AiDatasetKey.Recurring] = new AiDatasetState(AiDatasetStatus.Available);
        }
        if (queryPlan.NeedsLoans)
        {
            datasetStates[AiDatasetKey.Loans] = sensitiveMode
                ? new AiDatasetState(AiDatasetStatus.Hidden, Reason: "hidden by sensitive mode")
                : new AiDatasetState(AiDatasetStatus.Available);
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
}
