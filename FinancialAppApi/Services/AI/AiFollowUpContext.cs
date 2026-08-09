using System.Globalization;
using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

// Conversation-frame helpers that make a follow-up self-contained without replaying prior prose.
// The frame carries only validated query parameters and operation names; live financial records are
// always loaded again from the database for the newly resolved scope.
public partial class AiAssistantService
{
    private const string TransactionTopic = "transactional";
    private const string WishlistTopic = "wishlist";
    private const string RecurringTopic = "recurring";
    private const string RewardsTopic = "rewards";
    private const string InvestmentTopic = "investment";
    private const string ReportTopic = "report";

    private static readonly HashSet<string> KnownConversationTopics = new(StringComparer.Ordinal)
    {
        TransactionTopic, WishlistTopic, RecurringTopic, RewardsTopic, InvestmentTopic, ReportTopic
    };

    // These facets preserve the *kind* of answer requested, not just its data scope. This is what
    // stops "duplicates/anomalies/daily high this cycle" -> "previous cycle" from degrading into
    // a generic cycle total. Values are deliberately closed-vocabulary because state is untrusted.
    private static readonly HashSet<string> KnownQueryFacets = new(StringComparer.Ordinal)
    {
        "anomaly", "duplicates", "activity_count", "list", "total", "average", "comparison",
        "daily_extreme", "largest", "smallest", "latest", "earliest", "balance_snapshot",
        "ledger_forecast", "allocation_performance", "recurring_cost", "recurring_upcoming",
        "recurring_status", "wishlist_affordability", "wishlist_forecast", "stability_progress",
        "group_category", "group_ledger", "group_merchant", "group_day"
    };

    private static readonly HashSet<string> ModifierFacets = new(StringComparer.Ordinal)
    {
        "group_category", "group_ledger", "group_merchant", "group_day"
    };

    private static readonly HashSet<string> TransactionFacets = new(StringComparer.Ordinal)
    {
        "anomaly", "duplicates", "activity_count", "list", "total", "average", "comparison",
        "daily_extreme", "largest", "smallest", "latest", "earliest", "balance_snapshot",
        "ledger_forecast", "allocation_performance", "stability_progress",
        "group_category", "group_ledger", "group_merchant", "group_day"
    };

    private static readonly HashSet<string> WishlistFacets = new(StringComparer.Ordinal)
    {
        "list", "wishlist_affordability", "wishlist_forecast"
    };

    private static readonly HashSet<string> RecurringFacets = new(StringComparer.Ordinal)
    {
        "list", "recurring_cost", "recurring_upcoming", "recurring_status", "group_category", "group_ledger"
    };

    internal static IReadOnlyList<string> DetectQueryFacets(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var facets = new List<string>();
        void Add(string value)
        {
            if (!facets.Contains(value, StringComparer.Ordinal)) facets.Add(value);
        }

        if (Regex.IsMatch(text, @"\b(unusual|unexpected|anomal(?:y|ies|ous)?|spikes?|outliers?)\b", RegexOptions.IgnoreCase)) Add("anomaly");
        if (Regex.IsMatch(text, @"\b(duplicates?|twice|double[ -]charged|charged twice|repeated charges?)\b", RegexOptions.IgnoreCase)) Add("duplicates");
        if (Regex.IsMatch(text, @"\b(?:which|what)\s+day\b.{0,50}\b(most|highest|largest|lowest|least)\b|\b(most|highest|largest)\b.{0,30}\b(day|daily)\b", RegexOptions.IgnoreCase)) Add("daily_extreme");
        if (Regex.IsMatch(text, @"\b(wallet balance|ledger (?:category )?(?:balance|balances)|most balance|highest balance|balance right now)\b", RegexOptions.IgnoreCase)) Add("balance_snapshot");
        if (TryParseLedgerBalanceForecast(text) != null) Add("ledger_forecast");
        if (Regex.IsMatch(text, @"\bstability\b.{0,45}\b(fund|goal|target|on track|progress|reach(?:ed)?|close|there yet|percent)\b|\b(goal|target|on track|progress|reach(?:ed)?|percent)\b.{0,45}\bstability\b", RegexOptions.IgnoreCase)) Add("stability_progress");

        var recurring = RecurringSignal.IsMatch(text);
        if (recurring && WantsRecurringCostSummary(text)) Add("recurring_cost");
        if (recurring && Regex.IsMatch(text, @"\b(next|upcoming|due next|renew(?:s|al)?|coming up)\b", RegexOptions.IgnoreCase)) Add("recurring_upcoming");
        if (recurring && DetectRecurringStatus(text) != null) Add("recurring_status");

        var wishlist = WishlistSignal.IsMatch(text);
        if (wishlist && Regex.IsMatch(text, @"\b(how many|which|what|can i)\b.{0,35}\b(afford|buy|get)\b|\baffordable\b", RegexOptions.IgnoreCase)) Add("wishlist_affordability");
        if (wishlist && WishlistForecastSignal.IsMatch(text)) Add("wishlist_forecast");

        if (ImprovementSignal.IsMatch(text)) Add("allocation_performance");
        if (Regex.IsMatch(text, @"\b(compare|comparison|vs\.?|versus|trend|history|historical|over time|each month|every month|month over month|year over year)\b|\b(?:higher|lower|better|worse|more|less)\s+than\b", RegexOptions.IgnoreCase)) Add("comparison");
        if (CountQuestionSignal.IsMatch(text)) Add("activity_count");
        if (Regex.IsMatch(text, @"\b(show|list|find|search|which|what)\b.{0,45}\b(transactions?|purchases?|payments?|charges?|records?|entries|subscriptions?|bills?|wishlist items?)\b", RegexOptions.IgnoreCase)) Add("list");
        if (Regex.IsMatch(text, @"\b(show|list|find)\s+(?:me\s+)?(?:my\s+)?(?:income|inflows?|outflows?|expenses?|spending|transfers?)\b", RegexOptions.IgnoreCase)) Add("list");
        if (Regex.IsMatch(text, @"\b(average|averages|avg|mean)\b", RegexOptions.IgnoreCase)) Add("average");
        if (Regex.IsMatch(text, @"\b(total|totals|sum|altogether|in all)\b|\bhow much\b", RegexOptions.IgnoreCase)) Add("total");
        if (Regex.IsMatch(text, @"\b(largest|biggest|highest|most expensive|maximum|max)\b", RegexOptions.IgnoreCase)) Add("largest");
        if (Regex.IsMatch(text, @"\b(smallest|lowest|cheapest|least expensive|minimum|min)\b", RegexOptions.IgnoreCase)) Add("smallest");
        if (Regex.IsMatch(text, @"\b(latest|most recent|newest|last transaction)\b", RegexOptions.IgnoreCase)) Add("latest");
        if (Regex.IsMatch(text, @"\b(earliest|oldest|first transaction)\b", RegexOptions.IgnoreCase)) Add("earliest");
        if (Regex.IsMatch(text, @"\b(?:break(?:down)?|group(?:ed)?|split|by)\b.{0,25}\b(?:transaction )?categor(?:y|ies)\b|\bper category\b", RegexOptions.IgnoreCase)) Add("group_category");
        if (Regex.IsMatch(text, @"\b(?:break(?:down)?|group(?:ed)?|split|by)\b.{0,25}\bledger(?: category)?\b|\bper ledger\b", RegexOptions.IgnoreCase)) Add("group_ledger");
        if (Regex.IsMatch(text, @"\b(?:break(?:down)?|group(?:ed)?|split|by)\b.{0,25}\b(merchant|vendor|store|shop)\b|\bper merchant\b", RegexOptions.IgnoreCase)) Add("group_merchant");
        if (Regex.IsMatch(text, @"\b(?:break(?:down)?|group(?:ed)?|split|by)\b.{0,25}\b(day|date|daily)\b|\bper day\b", RegexOptions.IgnoreCase)) Add("group_day");

        return facets;
    }

    private static string CanonicalFacetClause(string facet, string? recurringStatus) => facet switch
    {
        "anomaly" => "unusual spending anomalies",
        "duplicates" => "duplicate transactions",
        "activity_count" => "how many matching transactions",
        "list" => "list the matching transactions",
        "total" => "total spending",
        "average" => "average spending",
        "comparison" => "compare the requested cycles",
        "daily_extreme" => "which day had the most spending",
        "largest" => "largest transaction",
        "smallest" => "smallest transaction",
        "latest" => "latest transactions",
        "earliest" => "earliest transactions",
        "balance_snapshot" => "ledger balances right now",
        "ledger_forecast" => "how long until the ledger reaches the target",
        "allocation_performance" => "financial performance advice",
        "recurring_cost" => "recurring subscription cost per month",
        "recurring_upcoming" => "upcoming recurring subscriptions",
        "recurring_status" => $"{recurringStatus ?? "current"} recurring bill status",
        "wishlist_affordability" => "which wishlist items can I afford",
        "wishlist_forecast" => "wishlist savings forecast",
        "stability_progress" => "stability fund goal progress",
        "group_category" => "group by category",
        "group_ledger" => "group by ledger category",
        "group_merchant" => "group by merchant",
        "group_day" => "group by day",
        _ => string.Empty
    };

    private static IReadOnlyList<string> InheritedFacetClauses(
        string message,
        AiConversationState priorState,
        QueryFamily family)
    {
        var prior = (priorState.LastQueryFacets ?? [])
            .Where(KnownQueryFacets.Contains)
            .Where(family switch
            {
                QueryFamily.Wishlist => WishlistFacets.Contains,
                QueryFamily.Recurring => RecurringFacets.Contains,
                QueryFamily.Transactional => TransactionFacets.Contains,
                _ => _ => false
            })
            .ToList();
        if (prior.Count == 0) return [];

        var current = DetectQueryFacets(message);
        var currentHasPrimary = current.Any(f => !ModifierFacets.Contains(f));
        var currentHasModifier = current.Any(ModifierFacets.Contains);
        var inherited = prior.Where(f => ModifierFacets.Contains(f) ? !currentHasModifier : !currentHasPrimary);
        return inherited
            .Select(f => f == "list"
                ? family switch
                {
                    QueryFamily.Wishlist => "list wishlist items",
                    QueryFamily.Recurring => "list recurring subscriptions",
                    _ => CanonicalFacetClause(f, priorState.LastRecurringStatus)
                }
                : CanonicalFacetClause(f, priorState.LastRecurringStatus))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();
    }

    private static string? DetermineConversationTopic(
        string message,
        IReadOnlyList<string> intentNames,
        AiConversationState? priorState)
    {
        if (intentNames.Any(i => i.StartsWith("savings_goal.", StringComparison.OrdinalIgnoreCase) ||
                                 i.StartsWith("rewards.", StringComparison.OrdinalIgnoreCase)) ||
            NeedsRewardsSignal(message)) return RewardsTopic;
        if (intentNames.Any(i => i.StartsWith("investment.", StringComparison.OrdinalIgnoreCase)) ||
            InvestmentCoreSignal.IsMatch(message)) return InvestmentTopic;
        if (intentNames.Any(i => i.StartsWith("report.", StringComparison.OrdinalIgnoreCase)) ||
            NeedsReportSignal(message)) return ReportTopic;
        if (WishlistSignal.IsMatch(message)) return WishlistTopic;
        if (RecurringSignal.IsMatch(message)) return RecurringTopic;
        if (ExplicitTransactionDomainSignal.IsMatch(message) || TryParseAmountThreshold(message) != null) return TransactionTopic;
        if (intentNames.Any(i => i.StartsWith("wishlist.", StringComparison.OrdinalIgnoreCase))) return WishlistTopic;
        if (intentNames.Any(i => i.StartsWith("recurring.", StringComparison.OrdinalIgnoreCase))) return RecurringTopic;
        if (intentNames.Any(i =>
                i.StartsWith("ledger.", StringComparison.OrdinalIgnoreCase) ||
                i.StartsWith("allocation.", StringComparison.OrdinalIgnoreCase) ||
                i.Equals("category_limits.analysis", StringComparison.OrdinalIgnoreCase) ||
                i.Equals("cycle.insights", StringComparison.OrdinalIgnoreCase)))
            return TransactionTopic;
        return NeedsHistoryContext(message) ? priorState?.LastTopic : null;
    }

    private static readonly Regex ExplicitTransactionDomainSignal = new(
        @"\b(transactions?|ledger|spending|spent|expenses?|income|inflow|outflow|cash ?flow|deposits?|withdrawals?|merchant|purchase|charge|refund|transfer)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsSelfContainedFinancialRequest(string message) =>
        NeedsRewardsSignal(message) || InvestmentCoreSignal.IsMatch(message) || NeedsReportSignal(message) ||
        WishlistSignal.IsMatch(message) || RecurringSignal.IsMatch(message) ||
        ExplicitTransactionDomainSignal.IsMatch(message) || CategoryLimitSignal.IsMatch(message) ||
        CycleInsightSignal.IsMatch(message) ||
        Regex.IsMatch(message, @"\b(open|navigate|go to|add|create|edit|update)\b.{0,30}\b(dashboard|settings|wishlist|ledger|bill|subscription|transaction)\b", RegexOptions.IgnoreCase);

    private static readonly Regex ContinuationModifierSignal = new(
        @"^(?:and|also|then|now|next|but|actually|instead|alternatively|plus|okay|ok|same|again|still)\b" +
        @"|^(?:could|can|would)\s+you\s+(?:do|show|run|repeat|use|switch|change)\b|^(?:please\s+)?(?:do|show|run|repeat|use|switch|change)\s+(?:that|this|it|the same|same|the previous|previous|last|next|again)\b" +
        @"|\b(?:same (?:thing|question|analysis|period|scope)|do that|do the same|repeat that|as well|instead(?: of)?|rather than|previous result|earlier result|above result|former|latter)\b|\b(?:too|again)\s*(?:please)?\s*[?!.]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ClearAmountFilterSignal = new(
        @"\b(?:any|all) amounts?\b|\bregardless of amount\b|\b(?:remove|clear|drop|without|no) (?:the )?(?:amount |price )?(?:threshold|minimum|maximum|limit|filter)\b|\bno (?:minimum|maximum) amount\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ClearSearchSignal = new(
        @"\b(?:all|any) (?:merchants?|vendors?|stores?|shops?|transactions?)\b|\bregardless of (?:merchant|vendor|description)\b|\bnot (?:just|only)\b.{0,40}\b|\b(?:remove|clear|drop) (?:the )?(?:merchant|search|description) filter\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReplaceFilterSignal = new(
        @"\b(?:instead|rather than|replace|switch (?:it )?to|change (?:it )?to)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExplicitTransactionTypeSignal = new(
        @"\b(income|inflow|inflows|earnings?|salary|paychecks?|deposits?|received|credited?|outflow|outflows|expenses?|spending|spent|withdrawals?|debited?|transfers?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool WantsClearAmountFilter(string message) => ClearAmountFilterSignal.IsMatch(message);
    private static bool WantsClearSearch(string message) => WantsClearFilters(message) || ClearSearchSignal.IsMatch(message);
    private static bool WantsReplaceFilters(string message) => ReplaceFilterSignal.IsMatch(message);
    private static bool MessageMentionsTransactionType(string message) => ExplicitTransactionTypeSignal.IsMatch(message);

    private static string? DetectLedgerCategory(string? text) => string.IsNullOrWhiteSpace(text)
        ? null
        : LedgerCategories.FirstOrDefault(category =>
            Regex.IsMatch(text, $@"\b{Regex.Escape(category)}\b", RegexOptions.IgnoreCase));

    private static string? DetectRecurringStatus(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (Regex.IsMatch(text, @"\b(discard(?:ed)?|skip(?:ped)?)\b", RegexOptions.IgnoreCase)) return "discarded";
        if (Regex.IsMatch(text, @"\b(overdue|unpaid|not paid|missed|pending)\b", RegexOptions.IgnoreCase)) return "pending";
        if (Regex.IsMatch(text, @"\bpaid\b", RegexOptions.IgnoreCase)) return "paid";
        if (Regex.IsMatch(text, @"\binactive|disabled|paused\b", RegexOptions.IgnoreCase)) return "inactive";
        if (Regex.IsMatch(text, @"\bactive|enabled\b", RegexOptions.IgnoreCase)) return "active";
        return null;
    }

    private static string? DetectWishlistStatus(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (Regex.IsMatch(text, @"\b(not purchased|unpurchased|not bought|still saving|outstanding)\b", RegexOptions.IgnoreCase)) return "unpurchased";
        if (Regex.IsMatch(text, @"\b(purchased|bought|completed)\b", RegexOptions.IgnoreCase)) return "purchased";
        if (Regex.IsMatch(text, @"\baffordable|can afford\b", RegexOptions.IgnoreCase)) return "affordable";
        if (Regex.IsMatch(text, @"\binactive|archived\b", RegexOptions.IgnoreCase)) return "inactive";
        if (Regex.IsMatch(text, @"\bactive\b", RegexOptions.IgnoreCase)) return "active";
        return null;
    }

    private static string CanonicalTransactionType(string transactionType) => transactionType switch
    {
        "inflow" => "income inflow transactions",
        "outflow" => "outflow spending transactions",
        "transfer" => "transfer transactions",
        _ => string.Empty
    };

    private static string? ExtractStandaloneFollowUpSearchText(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var candidate = Regex.Replace(message.Trim(), @"[?!.]+$", string.Empty).Trim();
        candidate = Regex.Replace(candidate,
            @"^(?:and|also|then|now|but|actually|instead|what about|how about|what of|only|just)\s+",
            string.Empty, RegexOptions.IgnoreCase).Trim();
        candidate = Regex.Replace(candidate, @"\s+(?:instead|too|as well|please|again)$", string.Empty, RegexOptions.IgnoreCase).Trim();
        var words = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 1 or > 4) return null;
        if (words.All(word => decimal.TryParse(word, NumberStyles.Number, CultureInfo.InvariantCulture, out _))) return null;
        if (MessageMentionsCycle(candidate) || IsRelativeCyclePhrase(candidate) || MessageMentionsExactDate(candidate) ||
            TryParseAmountThreshold(candidate) != null || DetectQueryFacets(candidate).Count > 0 ||
            ExplicitTransactionTypeSignal.IsMatch(candidate) || WishlistSignal.IsMatch(candidate) ||
            RecurringSignal.IsMatch(candidate) || Regex.IsMatch(candidate, @"\b(include|exclude|without|only|filter|sort|group|breakdown)\b", RegexOptions.IgnoreCase))
        {
            return null;
        }
        if (words.All(word => SearchNoiseTerms.Contains(word))) return null;
        return NormalizeSearchText(candidate);
    }

    private static readonly Regex ExactDateSignal = new(
        $@"\b\d{{4}}-\d{{1,2}}-\d{{1,2}}\b|\b(?:{MonthNamePattern})\s+\d{{1,2}}(?:st|nd|rd|th)?(?:,?\s+\d{{4}})?\b|\b\d{{1,2}}(?:st|nd|rd|th)?\s+(?:{MonthNamePattern})(?:,?\s+\d{{4}})?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool MessageMentionsExactDate(string message) => ExactDateSignal.IsMatch(message);

    private static string? ResolveRelativeDate(string message, string? priorExactDate)
    {
        if (!DateOnly.TryParseExact(priorExactDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var anchor)) return null;
        int? days = Regex.IsMatch(message, @"\b(day|one) before(?: that| this| it)?\b|\bprevious day\b", RegexOptions.IgnoreCase) ? -1
            : Regex.IsMatch(message, @"\b(day|one) after(?: that| this| it)?\b|\bnext day\b", RegexOptions.IgnoreCase) ? 1
            : null;
        if (days.HasValue) return anchor.AddDays(days.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (Regex.IsMatch(message, @"\bsame day\b.{0,20}\b(last|previous) month\b", RegexOptions.IgnoreCase))
            return anchor.AddMonths(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (Regex.IsMatch(message, @"\bsame day\b.{0,20}\bnext month\b", RegexOptions.IgnoreCase))
            return anchor.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (Regex.IsMatch(message, @"\bsame (?:day|date)\b.{0,20}\b(last|previous) year\b", RegexOptions.IgnoreCase))
            return anchor.AddYears(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (Regex.IsMatch(message, @"\bsame (?:day|date)\b.{0,20}\bnext year\b", RegexOptions.IgnoreCase))
            return anchor.AddYears(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return null;
    }

    private static void AppendCycleAndDateContext(
        List<string> clauses,
        string message,
        AiConversationState priorState)
    {
        // Exact-day references are more specific than cycles. Resolve "next day" / "same day last
        // month" against the typed prior date before considering the prior cycle frame.
        var relativeDate = ResolveRelativeDate(message, priorState.LastExactDate);
        if (relativeDate != null)
        {
            clauses.Add(relativeDate);
            return;
        }
        if (MessageMentionsExactDate(message)) return;

        // Transformations that explicitly refer to the prior frame ("compare that with the next
        // cycle", "same period last year") take priority over the raw relative words in the new
        // message. Concrete keys are appended; ResolveTargetCycles always gives them precedence.
        var overrideKeys = ResolveFollowUpCycleOverride(message, priorState);
        if (overrideKeys is { Count: > 0 })
        {
            clauses.AddRange(overrideKeys);
            return;
        }
        if (MessageMentionsCycle(message)) return;

        if (!string.IsNullOrWhiteSpace(priorState.LastExactDate))
        {
            clauses.Add(priorState.LastExactDate);
            return;
        }
        if (priorState.LastResolvedCycleKeys is { Count: > 0 } cycleKeys)
        {
            clauses.AddRange(cycleKeys.Where(IsValidCycleKey));
            return;
        }
        if (!string.IsNullOrWhiteSpace(priorState.LastCycleHint))
        {
            // Backward compatibility for state emitted before concrete cycle keys existed.
            clauses.Add(priorState.LastCycleHint);
        }
    }

    private static IReadOnlyList<string>? ResolveFollowUpCycleOverride(string message, AiConversationState priorState)
    {
        var keys = priorState.LastResolvedCycleKeys?.Where(IsValidCycleKey).ToList();
        if (keys is not { Count: > 0 }) return null;

        int? yearShift = Regex.IsMatch(message, @"\b(?:same|corresponding) (?:period|cycle|month|range)\b.{0,25}\b(?:last|previous) year\b|\b(?:a|one) year (?:ago|earlier)\b", RegexOptions.IgnoreCase) ? -12
            : Regex.IsMatch(message, @"\b(?:same|corresponding) (?:period|cycle|month|range)\b.{0,25}\bnext year\b|\b(?:a|one) year later\b", RegexOptions.IgnoreCase) ? 12
            : null;
        if (yearShift.HasValue)
        {
            return keys.Select(ParseCycleKey).Where(c => c != null).Select(c =>
            {
                var shifted = AddMonths(c!.Year, c.MonthIndex, yearShift.Value);
                return FormatCycleKey(new CycleKey(shifted.Year, shifted.MonthIndex));
            }).ToList();
        }

        if (keys.Count > 1)
        {
            if (Regex.IsMatch(message, @"\b(first|former|earliest) (?:one|cycle|month)?\b", RegexOptions.IgnoreCase)) return [keys[0]];
            if (Regex.IsMatch(message, @"\b(last|latter|final|latest) (?:one|cycle|month)?\b", RegexOptions.IgnoreCase)) return [keys[^1]];
            return null;
        }

        var anchor = ParseCycleKey(keys[0]);
        if (anchor == null) return null;
        var compareRelative = Regex.IsMatch(message, @"\b(compare|versus|vs\.?|against|difference)\b", RegexOptions.IgnoreCase);
        int? compareOffset = Regex.IsMatch(message, @"\b(previous|prior|last) (cycle|month)\b", RegexOptions.IgnoreCase) ? -1
            : Regex.IsMatch(message, @"\bnext (cycle|month)\b", RegexOptions.IgnoreCase) ? 1
            : null;
        if (compareRelative && compareOffset.HasValue)
        {
            var other = AddMonths(anchor.Year, anchor.MonthIndex, compareOffset.Value);
            return [FormatCycleKey(anchor), FormatCycleKey(new CycleKey(other.Year, other.MonthIndex))];
        }

        var relative = TryResolveRelativeToPriorCycle(message, keys);
        return relative == null ? null : [FormatCycleKey(relative)];
    }

    private static CycleKey ResolveCycleContainingDate(DateOnly date, int cycleDay)
    {
        foreach (var offset in new[] { 0, -1, 1 })
        {
            var candidate = AddMonths(date.Year, date.Month, offset);
            var range = CategoryAttributionService.GetCycleRange(candidate.Year, candidate.MonthIndex, cycleDay);
            var start = DateOnly.FromDateTime(range.start);
            var end = DateOnly.FromDateTime(range.end);
            if (date >= start && date <= end) return new CycleKey(candidate.Year, candidate.MonthIndex);
        }
        return new CycleKey(date.Year, date.Month);
    }

    private static string? FindMentionedEntityName(string text, IEnumerable<string> names) => names
        .Where(name => !string.IsNullOrWhiteSpace(name) && text.Contains(name, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(name => name.Length)
        .FirstOrDefault();
}
