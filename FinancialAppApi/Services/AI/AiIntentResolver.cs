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
    private static readonly Regex LedgerAccountSignal = new(
        @"\b(account|accounts|bank|banking|wallet|e-?wallet|cash\s+(?:account|wallet)|debit card|credit card)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool WantsLedgerAccountActivity(string message) =>
        Regex.IsMatch(message,
            @"\b(spend|spent|spending|activity|transaction|transactions|purchase|purchases|paid|payment|payments|inflow|outflow|deposit|debit|credit|history|how much did)\b",
            RegexOptions.IgnoreCase);

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
        bool NeedsCategoryLimits,
        bool NeedsCycleInsights,
        bool NeedsRewards,
        bool NeedsInvestments,
        bool NeedsReport,
        bool NeedsLoans,
        bool NeedsCount);

    private enum QueryFamily { Transactional, Wishlist, Recurring, Rewards, Investment, Report, Loan }

    // Two-phase family resolution. A short continuation ("how about last cycle", "what about the
    // next one") often carries no topic of its own, so deciding the family from the message alone
    // would wrongly reset a wishlist/recurring thread to the default (Transactional). Phase 1: if
    // the message names a topic, that wins. Phase 2 (continuation): inherit the prior turn's family
    // so the follow-up stays on the same subject. Only then is the frame applied and the expanded
    // request re-resolved.
    private static QueryFamily ResolveContinuationFamily(string message, AiConversationState? priorState)
    {
        if (NeedsRewardsSignal(message)) return QueryFamily.Rewards;
        if (InvestmentCoreSignal.IsMatch(message)) return QueryFamily.Investment;
        if (NeedsReportSignal(message)) return QueryFamily.Report;
        if (LoanSignal.IsMatch(message)) return QueryFamily.Loan;
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
            if (topic == RewardsTopic || intents.Any(i =>
                    i.StartsWith("rewards.", StringComparison.Ordinal) ||
                    i.StartsWith("savings_goal.", StringComparison.Ordinal)))
                return QueryFamily.Rewards;
            if (topic == InvestmentTopic || intents.Any(i => i.StartsWith("investment.", StringComparison.Ordinal)))
                return QueryFamily.Investment;
            if (topic == ReportTopic || intents.Any(i => i.StartsWith("report.", StringComparison.Ordinal)))
                return QueryFamily.Report;
            if (topic == LoanTopic || intents.Any(i => i.StartsWith("loan.", StringComparison.Ordinal)))
                return QueryFamily.Loan;
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
        AiIntent.LedgerAnomaly or AiIntent.LedgerDuplicates or AiIntent.LedgerActivityCount or AiIntent.LedgerPurchaseFrequency or
        AiIntent.LedgerComparison or AiIntent.CategoryLimits or AiIntent.CycleInsights or
        AiIntent.AllocationPerformance;

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
        ["ledger.merchant_search", "ledger.activity_count", "ledger.purchase_frequency", "ledger.spending_total", "ledger.transaction_list"];

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
        @"|\b(all|every|each)\s+(cycles?|months?)\b|\b(across|over|through(?:out)?|in)\s+all\b|\ball[- ]?time\b|\bhistorical\s+(?:data|history|records?|transactions?)\b|\ball\s+(?:saved\s+)?history\b" +
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
        if (family == QueryFamily.Rewards)
        {
            if (!NeedsRewardsSignal(message)) clauses.Add("savings goals and Rewards plan");
            return string.Join(" ", clauses);
        }
        if (family == QueryFamily.Investment)
        {
            if (!InvestmentCoreSignal.IsMatch(message)) clauses.Add("investment portfolio");
            if (!Regex.IsMatch(message, @"\b(?:1m|3m|6m|1y|3y|5y|all)\b", RegexOptions.IgnoreCase) &&
                !string.IsNullOrWhiteSpace(priorState.LastInvestmentRange))
                clauses.Add($"range {priorState.LastInvestmentRange}");
            return string.Join(" ", clauses);
        }
        if (family == QueryFamily.Report)
        {
            if (!NeedsReportSignal(message)) clauses.Add("report review");
            if (!MessageMentionsCycle(message) && !string.IsNullOrWhiteSpace(priorState.LastReportCycleKey))
                clauses.Add($"cycle {priorState.LastReportCycleKey}");
            return string.Join(" ", clauses);
        }
        if (family == QueryFamily.Loan)
        {
            if (!LoanSignal.IsMatch(message)) clauses.Add("loan payoff and interest summary");
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
        // An account question followed by a short cycle/filter continuation must keep the
        // selected account in the query plan. The account id is carried as validated state and
        // resolved again against the tenant's live rows; this marker only reactivates the intent.
        if (priorState.LastLedgerAccountId is { Length: > 0 } && !LedgerAccountSignal.IsMatch(message))
            clauses.Add("ledger account");
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
        var investmentDomain = InvestmentCoreSignal.IsMatch(lower);
        var rewardsDomain = NeedsRewardsSignal(lower);
        var needsCycleAnalysis = CycleAnalysisSignal.IsMatch(lower) && !investmentDomain && !rewardsDomain;
        var needsPurchaseFrequency = PurchaseFrequencySignal.IsMatch(lower);
        var needsCycleComparison = CycleComparisonSignal.IsMatch(lower) && !needsPurchaseFrequency;
        var needsImprovement = ImprovementSignal.IsMatch(lower) && !investmentDomain && !rewardsDomain;
        var needsCount = CountQuestionSignal.IsMatch(lower);
        var needsWishlist = WishlistSignal.IsMatch(lower) && (!rewardsDomain || ExplicitWishlistSignal.IsMatch(lower));
        var needsWishlistForecast = needsWishlist && WishlistForecastSignal.IsMatch(lower);
        var needsRecurring = RecurringSignal.IsMatch(lower);
        var needsCategoryLimits = CategoryLimitSignal.IsMatch(lower);
        var needsCycleInsights = CycleInsightSignal.IsMatch(lower);

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

        var needsCycleSummary = needsCycleAnalysis || needsCycleComparison || needsImprovement || needsCount ||
            needsWishlistForecast || asksDailyExtreme || needsCategoryLimits || needsCycleInsights ||
            NeedsReportSignal(lower) || NeedsRewardsSignal(lower);
        var needsBudgetTargets = (needsCycleAnalysis || needsImprovement) && !investmentDomain && !rewardsDomain;

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
            needsCategoryLimits,
            needsCycleInsights,
            rewardsDomain,
            investmentDomain || Regex.IsMatch(lower, @"\binvestment\b.{0,30}\b(allocation|basket|drift|target)\b", RegexOptions.IgnoreCase),
            NeedsReportSignal(lower),
            LoanSignal.IsMatch(lower),
            needsCount);
    }

    private static bool NeedsRewardsSignal(string message) =>
        Regex.IsMatch(message, @"\b(rewards?|saving goals?|savings goals?|earmarks?|free rewards?)\b", RegexOptions.IgnoreCase);

    private static bool NeedsReportSignal(string message) =>
        Regex.IsMatch(message, @"\b(report|findings|review|explain this cycle)\b", RegexOptions.IgnoreCase);

    // The deterministic resolver. Produces typed intents + a typed query plan with confidence.
}
