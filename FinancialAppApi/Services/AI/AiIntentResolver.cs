using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

// Phase 1: the authoritative intent-resolution stage. Deterministic keyword parsing produces a
// typed AiIntentPlan (typed intents + a typed AiQueryPlan describing exactly what data to load).
// The classifier is consulted only when the deterministic result is uncertain, and its output is
// merged in -- never allowed to remove the deterministic data-loading decisions. The typed
// resolution and query plan are the sole routing contracts.
public partial class AiAssistantService
{
    // The typed plan handed to context loading. Intents are typed; every data-loading decision
    // lives on QueryPlan; Constraints/ConversationState travel alongside.
    private sealed record AiIntentPlan(
        IReadOnlyList<AiIntent> Intents,
        double Confidence,
        bool UsedClassifier,
        AiQueryPlan QueryPlan,
        AiConversationState ConversationState,
        AiConstraints Constraints,
        AiIntentEntities? Entities = null);

    // Signal detection results, derived purely from the resolved query text (+ negation
    // constraints). Shared by the deterministic and classifier-merge paths so the regex signal
    // set is computed in exactly one place.
    private sealed record SignalNeeds(
        bool NeedsTransactionDetail,
        bool NeedsCycleSummary,
        bool NeedsCycleComparison,
        bool NeedsBudgetTargets,
        bool NeedsRecurring,
        bool NeedsWishlist,
        bool NeedsWishlistForecast,
        bool NeedsCount);

    // Folds recent user turns (+ structured prior state) into the text the signal parser reads,
    // so a short follow-up like "how much did those cost" still resolves the original topic.
    private static string BuildQueryText(
        string message,
        IReadOnlyList<AiChatMessage> history,
        AiConversationState? priorState)
    {
        var recentUserText = string.Join(
            " ",
            history.Where(m => m.Role == "user").Select(m => m.Content).TakeLast(3).Append(message));
        var queryText = NeedsHistoryContext(message) ? recentUserText : message;
        if (priorState != null && NeedsHistoryContext(message))
        {
            queryText = string.Join(" ", new[] { priorState.LastSearchText, priorState.LastCycleHint, queryText }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        }
        return queryText;
    }

    private static readonly Regex TransactionStateReferenceSignal = new(
        @"\b(those|these|them|that one|the highest one|the previous one|all of those|which of those|those ones|it|that|alone|only that|just that)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool UsesPriorTransactionState(string message) =>
        TransactionStateReferenceSignal.IsMatch(message);

    // Keyword heuristic, not a model call -- zero added latency/cost. A missed signal degrades
    // gracefully: the system instruction tells the model to ask a clarifying follow-up rather
    // than guess when it needs numbers that aren't in front of it. NeedsCycleSummary and
    // NeedsBudgetTargets are computed BEFORE negation is applied to the wishlist/recurring flags,
    // preserving the exact behavior of the previous ApplyNegatedTopics ordering.
    private static SignalNeeds ComputeSignalNeeds(string lower, AiConstraints constraints)
    {
        var needsCycleAnalysis = CycleAnalysisSignal.IsMatch(lower);
        var needsCycleComparison = CycleComparisonSignal.IsMatch(lower);
        var needsImprovement = ImprovementSignal.IsMatch(lower);
        var needsCount = CountQuestionSignal.IsMatch(lower);
        var needsWishlist = WishlistSignal.IsMatch(lower);
        var needsWishlistForecast = needsWishlist && WishlistForecastSignal.IsMatch(lower);
        var needsRecurring = RecurringSignal.IsMatch(lower);

        // The per-row detail sample is the largest prompt block, so it is only loaded when the
        // user actually wants individual records; a pure "how much/how many/total" question is
        // answered from cycle aggregates instead (unless a count, which still needs matches).
        var aggregateOnly = AggregateQuestionSignal.IsMatch(lower) && !ExplicitRecordSignal.IsMatch(lower);
        var asksDailyExtreme = Regex.IsMatch(lower, @"\b(which|what) day\b.*\b(most|highest|largest)\b|\bmost\b.*\b(day|daily)\b");
        var needsTransactionDetail = (TransactionDetailSignal.IsMatch(lower) || needsCount || asksDailyExtreme) &&
            (!aggregateOnly || needsCount);

        var needsCycleSummary = needsCycleAnalysis || needsCycleComparison || needsImprovement || needsCount || needsWishlistForecast || asksDailyExtreme;
        var needsBudgetTargets = needsCycleAnalysis || needsImprovement;

        // Negated topics ("I'm not asking about my wishlist") drop the matching data block so the
        // model is never handed context the user explicitly said they don't want.
        foreach (var topic in constraints.NegatedTopics)
        {
            if (topic.Contains("wishlist", StringComparison.OrdinalIgnoreCase) || topic.Contains("wish list", StringComparison.OrdinalIgnoreCase))
            {
                needsWishlist = false;
                needsWishlistForecast = false;
            }
            if (topic.Contains("recurring", StringComparison.OrdinalIgnoreCase) || topic.Contains("subscription", StringComparison.OrdinalIgnoreCase))
            {
                needsRecurring = false;
            }
        }

        return new SignalNeeds(
            needsTransactionDetail,
            needsCycleSummary,
            needsCycleComparison,
            needsBudgetTargets,
            needsRecurring,
            needsWishlist,
            needsWishlistForecast,
            needsCount);
    }

    // The deterministic resolver. Produces typed intents + a typed query plan with confidence.
    private static AiIntentPlan ResolveDeterministically(
        string message,
        IReadOnlyList<AiChatMessage> history,
        AiConversationState? priorState = null)
    {
        var queryText = BuildQueryText(message, history, priorState);
        var constraints = ParseConstraints(message);
        var lower = queryText.ToLowerInvariant();
        var s = ComputeSignalNeeds(lower, constraints);

        var intents = new List<AiIntent>();
        if (LooksLikeLedgerEditCommand(message)) intents.Add(AiIntent.LedgerEdit);
        if (Regex.IsMatch(lower, @"\b(add|create|record|log|enter)\b") && TransactionDetailSignal.IsMatch(lower)) intents.Add(AiIntent.LedgerAdd);
        if (s.NeedsWishlist && Regex.IsMatch(lower, @"\b(add|create|edit|update|change|modify)\b"))
            intents.Add(Regex.IsMatch(lower, @"\b(edit|update|change|modify)\b") ? AiIntent.WishlistEdit : AiIntent.WishlistAdd);
        if (s.NeedsRecurring && Regex.IsMatch(lower, @"\b(add|create|edit|update|change|modify)\b"))
            intents.Add(Regex.IsMatch(lower, @"\b(edit|update|change|modify)\b") ? AiIntent.RecurringEdit : AiIntent.RecurringAdd);
        if (CountQuestionSignal.IsMatch(lower)) intents.Add(AiIntent.LedgerActivityCount);
        if (ExplicitRecordSignal.IsMatch(lower) || TransactionDetailSignal.IsMatch(lower)) intents.Add(AiIntent.LedgerTransactionList);
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
        if (s.NeedsBudgetTargets) intents.Add(ImprovementSignal.IsMatch(lower) ? AiIntent.AllocationPerformance : AiIntent.AllocationBalance);
        if (Regex.IsMatch(lower, @"\b(open|show|go to|navigate|take me)\b")) intents.Add(AiIntent.Navigation);
        if (intents.Count == 0) intents.Add(AiIntent.General);

        var distinct = intents.Distinct().ToList();
        var confidence = distinct.Contains(AiIntent.General) ? 0.2 :
            (distinct.Count == 1 && !distinct.Contains(AiIntent.LedgerTransactionList) ? 0.9 : 0.78);

        // A merchant search always needs the matching rows even if the phrasing looked aggregate.
        var needsTransactionDetail = s.NeedsTransactionDetail || distinct.Contains(AiIntent.LedgerMerchantSearch);

        var intentNames = distinct.Select(ToIntentName).ToList();
        // A short follow-up carries no real search term of its own -- discard noise extractions
        // (bare pronouns/verbs) and fall back to the sanitized prior turn's state.
        var extractedSearch = ExtractLikelySearchText(queryText, intentNames);
        if (IsNoiseSearchTerm(extractedSearch)) extractedSearch = null;
        var searchText = extractedSearch ?? priorState?.LastSearchText;
        var cycleHint = ExtractConversationCycle(queryText) ?? priorState?.LastCycleHint;
        var transactionIds = UsesPriorTransactionState(message) ? priorState?.LastMatchedTransactionIds : null;
        var wishlistItemId = s.NeedsWishlist || UsesPriorTransactionState(message) ? priorState?.LastWishlistItemId : null;

        var plan = BuildQueryPlan(
            distinct, needsTransactionDetail, s.NeedsCycleSummary, s.NeedsCycleComparison,
            s.NeedsWishlist, s.NeedsWishlistForecast, s.NeedsRecurring, s.NeedsBudgetTargets,
            searchText, cycleHint, queryText, transactionIds, wishlistItemId);
        return new AiIntentPlan(
            distinct,
            confidence,
            false,
            plan,
            ResolveConversationState(history, intentNames, searchText, cycleHint, priorState),
            constraints,
            new AiIntentEntities(searchText, cycleHint, null, null, null, null, wishlistItemId,
                priorState?.LastWishlistReference, null, transactionIds ?? [],
                constraints.ExcludedCategories.Concat(constraints.ExcludeTransfers ? ["transfers"] : []).ToList()));
    }

    // Merges a validated classifier result over the deterministic signal baseline. The classifier
    // may ADD intents/search entities and raise data requirements; it can never lower the
    // deterministic data-loading decisions (union semantics) nor override safety constraints.
    private static AiIntentPlan MergeResolutions(
        string message,
        IReadOnlyList<AiChatMessage> history,
        IntentClassification classification,
        AiConversationState? priorState = null)
    {
        var baseQueryText = BuildQueryText(message, history, priorState);
        var parsedConstraints = ParseConstraints(message);
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

        var typedIntents = ParseIntents(classification.Intents);
        bool Has(AiIntent i) => typedIntents.Contains(i);
        var queryText = string.Join(" ", new[] { baseQueryText, classification.SearchText, classification.CycleHint,
                classification.Date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        var needsTransactionDetail = s.NeedsTransactionDetail || Has(AiIntent.LedgerActivityCount) || Has(AiIntent.LedgerMerchantSearch) ||
            Has(AiIntent.LedgerTransactionList) || Has(AiIntent.LedgerEdit) || Has(AiIntent.LedgerAnomaly) || Has(AiIntent.LedgerDuplicates);
        var needsCycleSummary = s.NeedsCycleSummary || Has(AiIntent.LedgerActivityCount) || Has(AiIntent.LedgerSpendingTotal) ||
            Has(AiIntent.LedgerComparison) || Has(AiIntent.WishlistForecast) || Has(AiIntent.AllocationBalance) || Has(AiIntent.AllocationPerformance);
        var needsCycleComparison = s.NeedsCycleComparison || Has(AiIntent.LedgerComparison);
        var needsBudgetTargets = s.NeedsBudgetTargets || Has(AiIntent.AllocationBalance) || Has(AiIntent.AllocationPerformance) || Has(AiIntent.WishlistForecast);
        var needsRecurring = s.NeedsRecurring || typedIntents.Any(i => i is AiIntent.RecurringList or AiIntent.RecurringUpcoming or AiIntent.RecurringAdd or AiIntent.RecurringEdit);
        var needsWishlist = s.NeedsWishlist || typedIntents.Any(i => i is AiIntent.WishlistList or AiIntent.WishlistForecast or AiIntent.WishlistAdd or AiIntent.WishlistEdit);
        var needsWishlistForecast = s.NeedsWishlistForecast || Has(AiIntent.WishlistForecast);

        var searchText = classification.SearchText ?? priorState?.LastSearchText;
        var cycleHint = classification.CycleHint ?? priorState?.LastCycleHint;
        var transactionIds = UsesPriorTransactionState(message) ? priorState?.LastMatchedTransactionIds : null;
        var wishlistItemId = needsWishlist || UsesPriorTransactionState(message) ? priorState?.LastWishlistItemId : null;
        var plan = BuildQueryPlan(
            typedIntents, needsTransactionDetail, needsCycleSummary, needsCycleComparison,
            needsWishlist, needsWishlistForecast, needsRecurring, needsBudgetTargets,
            searchText, cycleHint, queryText, transactionIds, wishlistItemId);
        return new AiIntentPlan(
            typedIntents,
            classification.Confidence,
            true,
            plan,
            ResolveConversationState(history, classification.Intents, classification.SearchText, classification.CycleHint, priorState),
            constraints,
            new AiIntentEntities(searchText, cycleHint, classification.Date, classification.Category,
                classification.LedgerCategory, classification.Amount, wishlistItemId, classification.WishlistReference,
                classification.TransactionReference, transactionIds ?? [],
                constraints.ExcludedCategories.Concat(constraints.ExcludeTransfers ? ["transfers"] : []).ToList()));
    }

}
