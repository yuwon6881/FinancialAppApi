using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    internal static AiIntentPlan ResolveDeterministically(
        string message,
        AiConversationState? priorState = null)
    {
        var queryText = BuildQueryText(message, priorState);
        // Constraints are parsed from the EXPANDED text so an inherited exclusion ("without
        // transfers" / "excluding food") appended by BuildQueryText is honored on a follow-up.
        var constraints = ParseConstraints(queryText);
        var lower = queryText.ToLowerInvariant();
        var s = ComputeSignalNeeds(lower, constraints);
        var mutationVerb = Regex.IsMatch(lower, @"\b(delete|remove|erase|purchase|buy|claim|unpurchase|undo purchase|mark (?:as )?paid|confirm (?:as )?paid|discard|skip|enable|disable|turn on|turn off|activate|deactivate|toggle)\b");
        var deleteVerb = Regex.IsMatch(lower, @"\b(delete|remove|erase)\b");

        var intents = new List<AiIntent>();
        if (LooksLikeLedgerEditCommand(message)) intents.Add(AiIntent.LedgerEdit);
        if (LedgerAccountSignal.IsMatch(lower)) intents.Add(AiIntent.LedgerAccount);
        if (deleteVerb && !s.NeedsWishlist && !s.NeedsRecurring && TransactionDetailSignal.IsMatch(lower)) intents.Add(AiIntent.LedgerEdit);
        if ((Regex.IsMatch(lower, @"\b(add|create|record|log|enter)\b") && TransactionDetailSignal.IsMatch(lower)) ||
            LooksLikeLedgerDraftList(message)) intents.Add(AiIntent.LedgerAdd);
        if (s.NeedsWishlist && (Regex.IsMatch(lower, @"\b(add|create|edit|update|change|modify)\b") || mutationVerb))
            intents.Add(Regex.IsMatch(lower, @"\b(add|create)\b") && !mutationVerb ? AiIntent.WishlistAdd : AiIntent.WishlistEdit);
        if (s.NeedsRecurring && (Regex.IsMatch(lower, @"\b(add|create|edit|update|change|modify)\b") || mutationVerb))
            intents.Add(Regex.IsMatch(lower, @"\b(add|create)\b") && !mutationVerb ? AiIntent.RecurringAdd : AiIntent.RecurringEdit);
        if (PurchaseFrequencySignal.IsMatch(lower)) intents.Add(AiIntent.LedgerPurchaseFrequency);
        else if (CountQuestionSignal.IsMatch(lower)) intents.Add(AiIntent.LedgerActivityCount);
        // An amount-threshold question ("which transaction exceeded 100", "purchases over 200") is a
        // request for the matching individual rows, so it is a transaction-list intent. Detecting it
        // here (before the cycle-analysis signal) also keeps the intent stable across a threshold
        // follow-up chain instead of drifting to spending_total when a turn happens to say "cycle".
        if (ExplicitRecordSignal.IsMatch(lower) || TransactionDetailSignal.IsMatch(lower) || TryParseAmountThreshold(lower) != null) intents.Add(AiIntent.LedgerTransactionList);
        if (Regex.IsMatch(lower, @"\b(merchant|shop|store|vendor|payments? to|purchase(?:s)? at|paid to)\b") &&
            (TransactionDetailSignal.IsMatch(lower) || ExplicitRecordSignal.IsMatch(lower))) intents.Add(AiIntent.LedgerMerchantSearch);
        if (Regex.IsMatch(lower, @"\b(?:show|find|search|latest|last)\s+[\p{L}\p{N}][\p{L}\p{N}'& -]{1,50}\s+(?:spending|purchase|purchases|transactions?|payments?)\b"))
            intents.Add(AiIntent.LedgerMerchantSearch);
        if (CycleAnalysisSignal.IsMatch(lower)) intents.Add(AiIntent.LedgerSpendingTotal);
        if (CycleComparisonSignal.IsMatch(lower)) intents.Add(AiIntent.LedgerComparison);
        if (Regex.IsMatch(lower, @"\b(unusual|unexpected|anomal|spike|outlier)\b")) intents.Add(AiIntent.LedgerAnomaly);
        if (Regex.IsMatch(lower, @"\b(duplicate|twice|double charged|charged twice)\b")) intents.Add(AiIntent.LedgerDuplicates);
        if (s.NeedsWishlistForecast) intents.Add(AiIntent.WishlistForecast);
        else if (s.NeedsWishlist) intents.Add(AiIntent.WishlistList);
        if (s.NeedsRecurring) intents.Add(Regex.IsMatch(lower, @"\b(next|upcoming|due|renew|renewal)\b") ? AiIntent.RecurringUpcoming : AiIntent.RecurringList);
        if (s.NeedsCategoryLimits) intents.Add(AiIntent.CategoryLimits);
        if (s.NeedsCycleInsights) intents.Add(AiIntent.CycleInsights);
        if (s.NeedsBudgetTargets) intents.Add(ImprovementSignal.IsMatch(lower) ? AiIntent.AllocationPerformance : AiIntent.AllocationBalance);
        if (s.NeedsRewards)
        {
            if (Regex.IsMatch(lower, @"\b(add|create|open)\s+(a\s+)?savings?\s+goal\b")) intents.Add(AiIntent.SavingsGoalAdd);
            else if (Regex.IsMatch(lower, @"\b(edit|update|change|modify)\b.*\b(goal|savings?)\b")) intents.Add(AiIntent.SavingsGoalEdit);
            else if (Regex.IsMatch(lower, @"\b(scenario|what if|change the target|change the deadline|move the deadline)\b")) intents.Add(AiIntent.SavingsGoalScenario);
            else if (Regex.IsMatch(lower, @"\b(pace|pacing|on track|deadline|funded|earmarked|commitment)\b")) intents.Add(AiIntent.SavingsGoalPacing);
            else if (Regex.IsMatch(lower, @"\b(goal|goals|earmark|earmarked|save)\b")) intents.Add(AiIntent.SavingsGoalList);
            else intents.Add(AiIntent.RewardsSummary);
        }
        if (s.NeedsInvestments)
        {
            if (Regex.IsMatch(lower, @"\b(allocation|basket|target|drift)\b")) intents.Add(AiIntent.InvestmentAllocation);
            else if (Regex.IsMatch(lower, @"\b(holding|holdings|position|instrument|fund|stock)\b")) intents.Add(AiIntent.InvestmentHolding);
            else intents.Add(AiIntent.InvestmentSummary);
        }
        if (s.NeedsReport) intents.Add(AiIntent.ReportReview);
        if (s.NeedsLoans) intents.Add(AiIntent.LoanSummary);
        if (Regex.IsMatch(lower, @"\b(open|show|go to|navigate|take me)\b")) intents.Add(AiIntent.Navigation);
        if (intents.Count == 0) intents.Add(AiIntent.General);

        var distinct = intents.Distinct().ToList();
        // Two-phase intent inheritance: a bare continuation ("and the one before that", "how about
        // last cycle") carries no intent keyword of its own and would resolve to General, which
        // strips the derived metrics from the answer. Adopt the prior turn's intent so the
        // follow-up is treated as the same kind of request it continues.
        if (distinct is [AiIntent.General] && NeedsHistoryContext(message) && priorState?.LastIntent is { } priorIntent)
        {
            var inheritedIntents = ParseIntents([priorIntent]);
            if (inheritedIntents.Count > 0 && !inheritedIntents.Contains(AiIntent.General))
            {
                distinct = inheritedIntents.ToList();
            }
        }
        distinct = CarryAnalyticalIntents(distinct, message, priorState);
        var confidence = distinct.Contains(AiIntent.General) ? 0.2 :
            (distinct.Count == 1 && !distinct.Contains(AiIntent.LedgerTransactionList) ? 0.9 : 0.78);

        // A merchant search always needs the matching rows even if the phrasing looked aggregate.
        // Keep aggregate questions aggregate-only even though incidental wording such as "did I"
        // may also add LedgerTransactionList. A genuine list follow-up carries the canonical word
        // "transactions", so ComputeSignalNeeds already raises detail for it.
        var needsAccountActivity = distinct.Contains(AiIntent.LedgerAccount) && WantsLedgerAccountActivity(lower);
        var needsTransactionDetail = s.NeedsTransactionDetail || needsAccountActivity || distinct.Contains(AiIntent.LedgerMerchantSearch) || distinct.Contains(AiIntent.LedgerEdit);

        var intentNames = distinct.Select(ToIntentName).ToList();
        // A short follow-up carries no real search term of its own -- discard noise extractions
        // (bare pronouns/verbs) and fall back to the sanitized prior turn's state.
        // Extract search from the ORIGINAL message, not the expanded text -- the canonical clauses
        // appended by BuildQueryText ("over 100", a yyyy-MM key) must never be recaptured as a
        // merchant/activity term. Inherited search is supplied explicitly below from the frame.
        var extractedSearch = ExtractLikelySearchText(message, intentNames);
        if (InheritsTransactionalContext(message, priorState))
            extractedSearch ??= ExtractStandaloneFollowUpSearchText(message);
        extractedSearch = StripAnalysisVocabulary(extractedSearch);
        if (IsNoiseSearchTerm(extractedSearch) || LooksLikeCycleOrAmountPhrase(extractedSearch)) extractedSearch = null;
        // Inherit prior search/cycle only on a transactional follow-up -- a wishlist/recurring turn
        // must not drag the previous transaction search or cycle in.
        var inheritsTxn = InheritsTransactionalContext(message, priorState);
        var searchText = extractedSearch ?? (inheritsTxn && !WantsClearSearch(message) ? priorState?.LastSearchText : null);
        var cycleHint = ExtractConversationCycle(queryText)
            ?? (distinct.Contains(AiIntent.LedgerPurchaseFrequency) ? "all history" : null)
            ?? (inheritsTxn ? priorState?.LastCycleHint : null);
        var transactionIds = UsesPriorTransactionState(message) ? priorState?.LastMatchedTransactionIds : null;
        var needsWishlist = s.NeedsWishlist || distinct.Any(i => i is AiIntent.WishlistList or AiIntent.WishlistForecast or AiIntent.WishlistAdd or AiIntent.WishlistEdit);
        var needsRecurring = s.NeedsRecurring || distinct.Any(i => i is AiIntent.RecurringList or AiIntent.RecurringUpcoming or AiIntent.RecurringAdd or AiIntent.RecurringEdit);
        var needsCycleSummary = s.NeedsCycleSummary || needsAccountActivity || distinct.Any(i => i is AiIntent.LedgerActivityCount or AiIntent.LedgerSpendingTotal or AiIntent.LedgerComparison or AiIntent.LedgerAnomaly or AiIntent.LedgerDuplicates or AiIntent.CategoryLimits or AiIntent.CycleInsights or AiIntent.AllocationBalance or AiIntent.AllocationPerformance);
        var needsBudgetTargets = s.NeedsBudgetTargets || distinct.Any(i => i is AiIntent.AllocationBalance or AiIntent.AllocationPerformance or AiIntent.WishlistForecast);
        var needsWishlistForecast = s.NeedsWishlistForecast || distinct.Contains(AiIntent.WishlistForecast);
        var wishlistItemId = needsWishlist || UsesPriorTransactionState(message) ? priorState?.LastWishlistItemId : null;
        // A bare continuation of a comparison ("and last cycle", "the one before that") keeps the
        // comparison scope so the follow-up is still rendered side-by-side.
        var needsCycleComparison = s.NeedsCycleComparison || (inheritsTxn && priorState?.LastComparison == true);

        var plan = BuildQueryPlan(
            distinct, needsTransactionDetail, needsCycleSummary, needsCycleComparison,
            needsWishlist, needsWishlistForecast, needsRecurring, needsBudgetTargets,
            s.NeedsCategoryLimits || distinct.Contains(AiIntent.CategoryLimits),
            s.NeedsCycleInsights || distinct.Contains(AiIntent.CycleInsights),
            searchText, cycleHint, queryText, transactionIds, wishlistItemId,
            needsLedgerAccounts: distinct.Contains(AiIntent.LedgerAccount));
        return new AiIntentPlan(
            distinct,
            confidence,
            false,
            plan,
            ResolveConversationState(message, intentNames, searchText, cycleHint, priorState),
            constraints,
            new AiIntentEntities(searchText, cycleHint, null, null, null, null, wishlistItemId,
                priorState?.LastWishlistReference, null, transactionIds ?? [],
                constraints.ExcludedCategories.Concat(constraints.ExcludeTransfers ? ["transfers"] : []).ToList(),
                LedgerAccountReference: priorState?.LastLedgerAccountId));
    }

    // Merges a validated classifier result over the deterministic signal baseline. The classifier
    // may ADD intents/search entities and raise data requirements; it can never lower the
    // deterministic data-loading decisions (union semantics) nor override safety constraints.
    private static AiIntentPlan MergeResolutions(
        string message,
        IntentClassification classification,
        AiConversationState? priorState = null)
    {
        var baseQueryText = BuildQueryText(message, priorState);
        var parsedConstraints = ParseConstraints(baseQueryText);
        var classifierConstraints = classification.Constraints ?? AiConstraints.None;
        // Deterministic safety rules always win; classifier constraints may only add a
        // restriction and can never remove a user negation or exclusion.
        var constraints = parsedConstraints with
        {
            PreventNavigation = parsedConstraints.PreventNavigation || classifierConstraints.PreventNavigation,
            ExcludeTransfers = parsedConstraints.ExcludeTransfers || classifierConstraints.ExcludeTransfers,
            ExcludedCategories = parsedConstraints.ExcludedCategories
                .Concat(classifierConstraints.ExcludedCategories)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList(),
            Hypothetical = parsedConstraints.Hypothetical || classifierConstraints.Hypothetical
        };
        var s = ComputeSignalNeeds(baseQueryText.ToLowerInvariant(), constraints);

        var typedIntents = CarryAnalyticalIntents(ParseIntents(classification.Intents).ToList(), message, priorState);
        if (s.NeedsLoans && !typedIntents.Contains(AiIntent.LoanSummary)) typedIntents.Add(AiIntent.LoanSummary);
        // Preserve the deterministic account marker in the classifier merge. A terse follow-up
        // can cause the model to return only a generic transaction intent even though the expanded
        // query still carries the selected account frame.
        if (LedgerAccountSignal.IsMatch(baseQueryText) && !typedIntents.Contains(AiIntent.LedgerAccount))
            typedIntents.Add(AiIntent.LedgerAccount);
        bool Has(AiIntent i) => typedIntents.Contains(i);
        var queryText = string.Join(" ", new[] { baseQueryText, classification.SearchText, classification.CycleHint,
                classification.Date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        var needsAccountActivity = Has(AiIntent.LedgerAccount) && WantsLedgerAccountActivity(baseQueryText);
        var needsTransactionDetail = s.NeedsTransactionDetail || needsAccountActivity || Has(AiIntent.LedgerActivityCount) || Has(AiIntent.LedgerPurchaseFrequency) || Has(AiIntent.LedgerMerchantSearch) ||
            Has(AiIntent.LedgerTransactionList) || Has(AiIntent.LedgerEdit) || Has(AiIntent.LedgerAnomaly) || Has(AiIntent.LedgerDuplicates);
        var needsCycleSummary = s.NeedsCycleSummary || needsAccountActivity || Has(AiIntent.ReportReview) || Has(AiIntent.LedgerActivityCount) || Has(AiIntent.LedgerSpendingTotal) ||
            Has(AiIntent.LedgerComparison) || Has(AiIntent.LedgerAnomaly) || Has(AiIntent.LedgerDuplicates) ||
            Has(AiIntent.WishlistForecast) || Has(AiIntent.CategoryLimits) ||
            Has(AiIntent.CycleInsights) || Has(AiIntent.AllocationBalance) || Has(AiIntent.AllocationPerformance);
        var needsBudgetTargets = s.NeedsBudgetTargets || Has(AiIntent.AllocationBalance) || Has(AiIntent.AllocationPerformance) || Has(AiIntent.WishlistForecast);
        var needsRecurring = s.NeedsRecurring || typedIntents.Any(i => i is AiIntent.RecurringList or AiIntent.RecurringUpcoming or AiIntent.RecurringAdd or AiIntent.RecurringEdit);
        var needsWishlist = s.NeedsWishlist || typedIntents.Any(i => i is AiIntent.WishlistList or AiIntent.WishlistForecast or AiIntent.WishlistAdd or AiIntent.WishlistEdit);
        var needsWishlistForecast = s.NeedsWishlistForecast || Has(AiIntent.WishlistForecast);
        var needsCategoryLimits = s.NeedsCategoryLimits || Has(AiIntent.CategoryLimits);
        var needsCycleInsights = s.NeedsCycleInsights || Has(AiIntent.CycleInsights);

        // Same transactional-continuation gating as the deterministic path.
        var inheritsTxn = InheritsTransactionalContext(message, priorState);
        var needsCycleComparison = s.NeedsCycleComparison || Has(AiIntent.LedgerComparison)
            || (inheritsTxn && priorState?.LastComparison == true);
        // The classifier can echo the analysis word back as a search entity too; sanitize it the
        // same way the deterministic path does rather than trusting the model's extraction.
        var classifiedSearch = StripAnalysisVocabulary(classification.SearchText);
        var searchText = classifiedSearch ?? (inheritsTxn ? priorState?.LastSearchText : null);
        var cycleHint = classification.CycleHint
            ?? (typedIntents.Contains(AiIntent.LedgerPurchaseFrequency) ? "all history" : null)
            ?? (inheritsTxn ? priorState?.LastCycleHint : null);
        var transactionIds = UsesPriorTransactionState(message) ? priorState?.LastMatchedTransactionIds : null;
        var wishlistItemId = needsWishlist || UsesPriorTransactionState(message) ? priorState?.LastWishlistItemId : null;
        var plan = BuildQueryPlan(
            typedIntents, needsTransactionDetail, needsCycleSummary, needsCycleComparison,
            needsWishlist, needsWishlistForecast, needsRecurring, needsBudgetTargets,
            needsCategoryLimits, needsCycleInsights,
            searchText, cycleHint, queryText, transactionIds, wishlistItemId,
            needsLedgerAccounts: Has(AiIntent.LedgerAccount));
        return new AiIntentPlan(
            typedIntents,
            classification.Confidence,
            true,
            plan,
            ResolveConversationState(message, typedIntents.Select(ToIntentName).ToList(), classifiedSearch, cycleHint, priorState),
            constraints,
            new AiIntentEntities(searchText, cycleHint, classification.Date, classification.Category,
                classification.LedgerCategory, classification.Amount, wishlistItemId, classification.WishlistReference,
                classification.TransactionReference, transactionIds ?? [],
                constraints.ExcludedCategories.Concat(constraints.ExcludeTransfers ? ["transfers"] : []).ToList(),
                classification.LedgerAccountReference ?? priorState?.LastLedgerAccountId));
    }
}
