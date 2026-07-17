using System.Globalization;
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
    internal sealed record AiIntentPlan(
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

    private enum QueryFamily { Transactional, Wishlist, Recurring }

    // Two-phase family resolution. A short continuation ("how about last cycle", "what about the
    // next one") often carries no topic of its own, so deciding the family from the message alone
    // would wrongly reset a wishlist/recurring thread to the default (Transactional). Phase 1: if
    // the message names a topic, that wins. Phase 2 (continuation): inherit the prior turn's family
    // so the follow-up stays on the same subject. Only then is the frame applied and the expanded
    // request re-resolved.
    private static QueryFamily ResolveContinuationFamily(string message, AiConversationState? priorState)
    {
        if (WishlistSignal.IsMatch(message)) return QueryFamily.Wishlist;
        if (RecurringSignal.IsMatch(message)) return QueryFamily.Recurring;
        // A real transaction/amount cue wins. A cycle by itself does not: recurring bill status is
        // cycle-scoped too, so "discarded bills this cycle" -> "previous cycle" must stay recurring.
        if (TryParseAmountThreshold(message) != null || ExplicitTransactionDomainSignal.IsMatch(message))
            return QueryFamily.Transactional;
        if (NeedsHistoryContext(message) && priorState != null)
        {
            var topic = priorState.LastTopic;
            var intents = priorState.LastIntents ?? (priorState.LastIntent == null ? [] : [priorState.LastIntent]);
            if (topic == RecurringTopic || intents.Any(i => i.StartsWith("recurring", StringComparison.Ordinal)))
                return QueryFamily.Recurring;
            // Wishlist data has no cycle dimension in this app. Preserve the existing, useful
            // behavior where a cycle-only request after wishlist returns to the transaction frame.
            if (!MessageMentionsCycle(message) &&
                (topic == WishlistTopic || intents.Any(i => i.StartsWith("wishlist", StringComparison.Ordinal))))
                return QueryFamily.Wishlist;
        }
        return QueryFamily.Transactional;
    }

    // "Special analysis" intents whose result is the kind of question being asked (not a default
    // total/list). A modifier-only follow-up ("how about previous cycle") must keep running these.
    private static bool IsAnalyticalIntent(AiIntent intent) => intent is
        AiIntent.LedgerAnomaly or AiIntent.LedgerDuplicates or AiIntent.LedgerActivityCount or
        AiIntent.LedgerComparison or AiIntent.AllocationPerformance;

    // On a transactional continuation that introduced no analysis of its own, re-apply the prior
    // turn's analytical intents to the new scope (fixes "unusual spending this cycle" -> "how about
    // previous cycle" collapsing to a plain outflow total).
    private static List<AiIntent> CarryAnalyticalIntents(List<AiIntent> current, string message, AiConversationState? priorState)
    {
        if (!InheritsTransactionalContext(message, priorState) || priorState?.LastIntents is not { } priorIntentNames)
            return current;
        if (current.Any(IsAnalyticalIntent)) return current;
        var carried = ParseIntents(priorIntentNames).Where(IsAnalyticalIntent).ToList();
        if (carried.Count == 0) return current;
        return current.Concat(carried).Distinct().ToList();
    }

    private static readonly string[] LedgerSearchIntentNames =
        ["ledger.merchant_search", "ledger.activity_count", "ledger.spending_total", "ledger.transaction_list"];

    // Every way ResolveTargetCycles keys a cycle off text. Detects only the PRESENCE of a cycle
    // reference in the current message (not its resolution), so we know whether to inherit the
    // prior turn's cycle. Relative references ("the one before that") deliberately DON'T match --
    // those carry no cycle of their own and are resolved against the prior frame instead.
    private static readonly Regex MessageMentionsCycleSignal = new(
        $@"\b(?:{MonthNamePattern})\s+(?:19|20)\d{{2}}\b" +                               // March 2023
        @"|\b(?:19|20)\d{2}-(?:0?[1-9]|1[0-2])\b" +                                       // 2023-03
        @"|\b(this|current|last|previous|prior|next)\s+(cycle|month)\b" +                 // this/last cycle
        @"|\b\d{1,2}\s+(?:cycles?|months?)\s+ago\b" +                                     // 3 cycles ago
        @"|\b(?:cycle|month)\s+before\s+last\b" +                                         // cycle before last
        @"|\b(?:last|past|previous|prior)\s+(?:\d{1,2}|few)\s+(?:cycles?|months?)\b" +     // last 3 cycles
        @"|\b(all|every|each)\s+(cycles?|months?)\b|\b(across|over|through(?:out)?|in)\s+all\b|\ball[- ]?time\b" +
        @"|\b(?:in|during|for|year)\s+(?:19|20)\d{2}\b" +                                 // in 2023
        @"|\b(last|previous|this|current)\s+year\b" +
        @"|\b(?:same|corresponding)\s+(?:period|cycle|month|range)\b" +
        @"|\b(?:next|following)\s+(?:cycle|month)\b" +
        @"|\b(?:q[1-4]|(?:first|second|third|fourth) quarter)(?:\s+(?:19|20)\d{2})?\b" +
        $@"|(?:\b(?:cycle|month|in|for|about|during|and|vs\.?|versus|with|against)\s+)(?:{MonthNamePattern})\b" + // cycle March / and May
        $@"|^\s*(?:(?:and|also|then|now|what about|how about|instead|just)\s+)?(?:{MonthNamePattern})(?:\s+(?:19|20)\d{{2}})?(?:\s+(?:instead|too|as well))?\s*[?!.]*\s*$", // bare "May?"
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool MessageMentionsCycle(string message) => MessageMentionsCycleSignal.IsMatch(message);

    private static bool MessageMentionsSearch(string message)
    {
        var extracted = ExtractLikelySearchText(message, LedgerSearchIntentNames);
        extracted ??= ExtractStandaloneFollowUpSearchText(message);
        return !string.IsNullOrWhiteSpace(extracted) && !IsNoiseSearchTerm(extracted)
            && !LooksLikeCycleOrAmountPhrase(extracted);
    }

    // Expands a short follow-up into a SELF-CONTAINED query by appending the prior turn's resolved
    // parameters -- in canonical, unambiguous forms -- for exactly the dimensions this message does
    // not itself name. This replaces the old approach of concatenating the prior message's raw
    // prose, which let stale wording ("this cycle") collide with the new turn ("last cycle") and
    // resolve to two cycles at once. Only inherits when this is a follow-up (NeedsHistoryContext)
    // and the dimension is compatible with the message's topic family. No prior conversation text
    // is included, so nothing extra is ever sent to the model.
    private static string BuildQueryText(string message, AiConversationState? priorState)
    {
        if (priorState == null || !NeedsHistoryContext(message)) return message;
        var family = ResolveContinuationFamily(message, priorState);

        var clauses = new List<string> { message };
        clauses.AddRange(InheritedFacetClauses(message, priorState, family));

        // Wishlist continuations inherit only wishlist operation/reference state, never transaction
        // cycle/threshold/exclusions. Add the topic explicitly so an inherited wishlist intent also
        // turns on the wishlist loader in the deterministic path.
        if (family == QueryFamily.Wishlist)
        {
            if (!WishlistSignal.IsMatch(message)) clauses.Add("wishlist");
            if (!MessageMentionsSearch(message) && !string.IsNullOrWhiteSpace(priorState.LastWishlistReference))
                clauses.Add(priorState.LastWishlistReference);
            if (DetectWishlistStatus(message) == null && !string.IsNullOrWhiteSpace(priorState.LastWishlistStatus))
                clauses.Add($"{priorState.LastWishlistStatus} wishlist items");
            return string.Join(" ", clauses);
        }
        if (family == QueryFamily.Recurring)
        {
            if (!RecurringSignal.IsMatch(message)) clauses.Add("recurring subscriptions");
            if (!MessageMentionsSearch(message) && !string.IsNullOrWhiteSpace(priorState.LastRecurringReference))
                clauses.Add(priorState.LastRecurringReference);
            if (DetectRecurringStatus(message) == null && !string.IsNullOrWhiteSpace(priorState.LastRecurringStatus))
                clauses.Add($"{priorState.LastRecurringStatus} bill status");
            AppendCycleAndDateContext(clauses, message, priorState);
            return string.Join(" ", clauses);
        }

        // Transactional continuation: inherit each dimension the message doesn't name, in canonical
        // form. Cycle is resolved to a concrete yyyy-MM key so the expanded text never contains both
        // a relative word and a key.
        AppendCycleAndDateContext(clauses, message, priorState);

        if (!WantsClearAmountFilter(message) && TryParseAmountThreshold(message) == null && FormatAmountThreshold(priorState.LastAmountThreshold) is { } thresholdText)
        {
            clauses.Add(thresholdText);
        }

        if (!WantsClearSearch(message) && !MessageMentionsSearch(message) && !string.IsNullOrWhiteSpace(priorState.LastSearchText))
        {
            clauses.Add(priorState.LastSearchText);
        }

        if (DetectLedgerCategory(message) == null && !string.IsNullOrWhiteSpace(priorState.LastLedgerCategory))
            clauses.Add($"{priorState.LastLedgerCategory} ledger");
        if (!MessageMentionsTransactionType(message) && !string.IsNullOrWhiteSpace(priorState.LastTransactionType))
            clauses.Add(CanonicalTransactionType(priorState.LastTransactionType));
        if (priorState.LastTargetAmount is > 0m &&
            priorState.LastQueryFacets?.Contains("ledger_forecast", StringComparer.Ordinal) == true &&
            !Regex.IsMatch(message, @"[\p{Sc}$]?\d[\d,]*(?:\.\d+)?"))
            clauses.Add($"target {priorState.LastTargetAmount.Value.ToString(CultureInfo.InvariantCulture)}");

        // Don't re-apply a prior filter the user is now lifting ("include transfers again", "show
        // everything").
        var clearsFilters = WantsClearFilters(message);
        var currentConstraints = ParseConstraints(message);
        var replacesFilters = WantsReplaceFilters(message);
        if (!ExcludeTransfersSignal.IsMatch(message) && priorState.LastExcludeTransfers && !WantsIncludeTransfers(message))
        {
            clauses.Add("without transfers");
        }
        if (!clearsFilters && !replacesFilters && priorState.LastExcludedCategories is { Count: > 0 } excluded)
        {
            clauses.AddRange(excluded.Select(category => $"excluding {category}"));
        }
        if (!clearsFilters && currentConstraints.IncludedCategories.Count == 0 && priorState.LastIncludedCategories is { Count: > 0 } included)
        {
            clauses.AddRange(included.Select(category => $"only {category}"));
        }

        return string.Join(" ", clauses);
    }

    // True when the message inherits transactional context from the prior frame -- used to gate the
    // explicit searchText/cycleHint/comparison fallbacks so they never leak into a wishlist/
    // recurring turn.
    private static bool InheritsTransactionalContext(string message, AiConversationState? priorState) =>
        priorState != null && NeedsHistoryContext(message)
        && ResolveContinuationFamily(message, priorState) == QueryFamily.Transactional;

    private static readonly Regex TransactionStateReferenceSignal = new(
        @"\b(those|these|them|that one|this one|the one|the highest one|the previous one|the (?:first|second|third|fourth|last|former|latter|largest|biggest|smallest|cheapest|latest|earliest|most expensive|pure|only) one|all of those|both of those|either of those|which of those|those ones|it|that|alone|only that|just that|the former|the latter)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool UsesPriorTransactionState(string message) =>
        TransactionStateReferenceSignal.IsMatch(message) && !IsRelativeCyclePhrase(message);

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
        // An amount comparison ("over 250", "under 50", "between 100 and 200") is a request for the
        // matching individual records, so it needs the detail sample even when the phrasing itself
        // read as aggregate ("how much did I spend over 100").
        var hasAmountThreshold = TryParseAmountThreshold(lower) != null;
        var needsTransactionDetail = (TransactionDetailSignal.IsMatch(lower) || needsCount || asksDailyExtreme || hasAmountThreshold) &&
            (!aggregateOnly || needsCount || hasAmountThreshold);

        var needsCycleSummary = needsCycleAnalysis || needsCycleComparison || needsImprovement || needsCount || needsWishlistForecast || asksDailyExtreme;
        var needsBudgetTargets = needsCycleAnalysis || needsImprovement;

        // Negated topics ("I'm not asking about my wishlist") drop the matching data block so the
        // model is never handed context the user explicitly said they don't want.
        var negated = constraints.NegatedTopics.Concat(constraints.ExcludedCategories);
        foreach (var topic in negated)
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
        if (deleteVerb && !s.NeedsWishlist && !s.NeedsRecurring && TransactionDetailSignal.IsMatch(lower)) intents.Add(AiIntent.LedgerEdit);
        if ((Regex.IsMatch(lower, @"\b(add|create|record|log|enter)\b") && TransactionDetailSignal.IsMatch(lower)) ||
            LooksLikeLedgerDraftList(message)) intents.Add(AiIntent.LedgerAdd);
        if (s.NeedsWishlist && (Regex.IsMatch(lower, @"\b(add|create|edit|update|change|modify)\b") || mutationVerb))
            intents.Add(Regex.IsMatch(lower, @"\b(add|create)\b") && !mutationVerb ? AiIntent.WishlistAdd : AiIntent.WishlistEdit);
        if (s.NeedsRecurring && (Regex.IsMatch(lower, @"\b(add|create|edit|update|change|modify)\b") || mutationVerb))
            intents.Add(Regex.IsMatch(lower, @"\b(add|create)\b") && !mutationVerb ? AiIntent.RecurringAdd : AiIntent.RecurringEdit);
        if (CountQuestionSignal.IsMatch(lower)) intents.Add(AiIntent.LedgerActivityCount);
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
        if (s.NeedsBudgetTargets) intents.Add(ImprovementSignal.IsMatch(lower) ? AiIntent.AllocationPerformance : AiIntent.AllocationBalance);
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
        var needsTransactionDetail = s.NeedsTransactionDetail || distinct.Contains(AiIntent.LedgerMerchantSearch) || distinct.Contains(AiIntent.LedgerEdit);

        var intentNames = distinct.Select(ToIntentName).ToList();
        // A short follow-up carries no real search term of its own -- discard noise extractions
        // (bare pronouns/verbs) and fall back to the sanitized prior turn's state.
        // Extract search from the ORIGINAL message, not the expanded text -- the canonical clauses
        // appended by BuildQueryText ("over 100", a yyyy-MM key) must never be recaptured as a
        // merchant/activity term. Inherited search is supplied explicitly below from the frame.
        var extractedSearch = ExtractLikelySearchText(message, intentNames);
        if (InheritsTransactionalContext(message, priorState))
            extractedSearch ??= ExtractStandaloneFollowUpSearchText(message);
        if (IsNoiseSearchTerm(extractedSearch) || LooksLikeCycleOrAmountPhrase(extractedSearch)) extractedSearch = null;
        // Inherit prior search/cycle only on a transactional follow-up -- a wishlist/recurring turn
        // must not drag the previous transaction search or cycle in.
        var inheritsTxn = InheritsTransactionalContext(message, priorState);
        var searchText = extractedSearch ?? (inheritsTxn && !WantsClearSearch(message) ? priorState?.LastSearchText : null);
        var cycleHint = ExtractConversationCycle(queryText) ?? (inheritsTxn ? priorState?.LastCycleHint : null);
        var transactionIds = UsesPriorTransactionState(message) ? priorState?.LastMatchedTransactionIds : null;
        var needsWishlist = s.NeedsWishlist || distinct.Any(i => i is AiIntent.WishlistList or AiIntent.WishlistForecast or AiIntent.WishlistAdd or AiIntent.WishlistEdit);
        var needsRecurring = s.NeedsRecurring || distinct.Any(i => i is AiIntent.RecurringList or AiIntent.RecurringUpcoming or AiIntent.RecurringAdd or AiIntent.RecurringEdit);
        var needsCycleSummary = s.NeedsCycleSummary || distinct.Any(i => i is AiIntent.LedgerActivityCount or AiIntent.LedgerSpendingTotal or AiIntent.LedgerComparison or AiIntent.LedgerAnomaly or AiIntent.LedgerDuplicates or AiIntent.AllocationBalance or AiIntent.AllocationPerformance);
        var needsBudgetTargets = s.NeedsBudgetTargets || distinct.Any(i => i is AiIntent.AllocationBalance or AiIntent.AllocationPerformance or AiIntent.WishlistForecast);
        var needsWishlistForecast = s.NeedsWishlistForecast || distinct.Contains(AiIntent.WishlistForecast);
        var wishlistItemId = needsWishlist || UsesPriorTransactionState(message) ? priorState?.LastWishlistItemId : null;
        // A bare continuation of a comparison ("and last cycle", "the one before that") keeps the
        // comparison scope so the follow-up is still rendered side-by-side.
        var needsCycleComparison = s.NeedsCycleComparison || (inheritsTxn && priorState?.LastComparison == true);

        var plan = BuildQueryPlan(
            distinct, needsTransactionDetail, needsCycleSummary, needsCycleComparison,
            needsWishlist, needsWishlistForecast, needsRecurring, needsBudgetTargets,
            searchText, cycleHint, queryText, transactionIds, wishlistItemId);
        return new AiIntentPlan(
            distinct,
            confidence,
            false,
            plan,
            ResolveConversationState(message, intentNames, searchText, cycleHint, priorState),
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
        bool Has(AiIntent i) => typedIntents.Contains(i);
        var queryText = string.Join(" ", new[] { baseQueryText, classification.SearchText, classification.CycleHint,
                classification.Date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        var needsTransactionDetail = s.NeedsTransactionDetail || Has(AiIntent.LedgerActivityCount) || Has(AiIntent.LedgerMerchantSearch) ||
            Has(AiIntent.LedgerTransactionList) || Has(AiIntent.LedgerEdit) || Has(AiIntent.LedgerAnomaly) || Has(AiIntent.LedgerDuplicates);
        var needsCycleSummary = s.NeedsCycleSummary || Has(AiIntent.LedgerActivityCount) || Has(AiIntent.LedgerSpendingTotal) ||
            Has(AiIntent.LedgerComparison) || Has(AiIntent.WishlistForecast) || Has(AiIntent.AllocationBalance) || Has(AiIntent.AllocationPerformance);
        var needsBudgetTargets = s.NeedsBudgetTargets || Has(AiIntent.AllocationBalance) || Has(AiIntent.AllocationPerformance) || Has(AiIntent.WishlistForecast);
        var needsRecurring = s.NeedsRecurring || typedIntents.Any(i => i is AiIntent.RecurringList or AiIntent.RecurringUpcoming or AiIntent.RecurringAdd or AiIntent.RecurringEdit);
        var needsWishlist = s.NeedsWishlist || typedIntents.Any(i => i is AiIntent.WishlistList or AiIntent.WishlistForecast or AiIntent.WishlistAdd or AiIntent.WishlistEdit);
        var needsWishlistForecast = s.NeedsWishlistForecast || Has(AiIntent.WishlistForecast);

        // Same transactional-continuation gating as the deterministic path.
        var inheritsTxn = InheritsTransactionalContext(message, priorState);
        var needsCycleComparison = s.NeedsCycleComparison || Has(AiIntent.LedgerComparison)
            || (inheritsTxn && priorState?.LastComparison == true);
        var searchText = classification.SearchText ?? (inheritsTxn ? priorState?.LastSearchText : null);
        var cycleHint = classification.CycleHint ?? (inheritsTxn ? priorState?.LastCycleHint : null);
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
            ResolveConversationState(message, typedIntents.Select(ToIntentName).ToList(), classification.SearchText, classification.CycleHint, priorState),
            constraints,
            new AiIntentEntities(searchText, cycleHint, classification.Date, classification.Category,
                classification.LedgerCategory, classification.Amount, wishlistItemId, classification.WishlistReference,
                classification.TransactionReference, transactionIds ?? [],
                constraints.ExcludedCategories.Concat(constraints.ExcludeTransfers ? ["transfers"] : []).ToList()));
    }

}
