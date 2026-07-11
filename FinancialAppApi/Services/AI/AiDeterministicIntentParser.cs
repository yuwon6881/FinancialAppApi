using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

// Phase 2: deterministic constraint parsing. These are the parts of a request that MUST be
// honored exactly and must never be delegated to the model's best-effort instruction
// following: negation ("don't open the ledger"), scope exclusions ("excluding rent",
// "without transfers"), and hypothetical framing ("what if I save RM200"). Extracted with
// keyword heuristics (zero model latency) and enforced downstream in query building and
// action validation.
public partial class AiAssistantService
{
    internal sealed record AiConstraints(
        bool PreventNavigation,
        bool ExcludeTransfers,
        IReadOnlyList<string> ExcludedCategories,
        IReadOnlyList<string> IncludedCategories,
        IReadOnlyList<string> NegatedTopics,
        bool Hypothetical)
    {
        public static readonly AiConstraints None = new(false, false, [], [], [], false);
    }

    // "don't/do not ... open/show/go to/navigate/take me" or an explicit "without opening".
    private static readonly Regex PreventNavigationSignal = new(
        @"\b(?:don'?t|do not|no need to|please don'?t|without)\b[^.?!]{0,40}?\b(open|show me|go to|navigate|take me|switch to)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExcludeTransfersSignal = new(
        @"\b(?:without|excluding|except|not including|ignore|ignoring|no|don'?t count|don'?t include)\s+(?:counting\s+)?transfers?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Follow-up that undoes a previously-applied filter so it isn't inherited forever: a global
    // clear ("show everything", "no filters") or an explicit transfer re-inclusion ("include
    // transfers again", "with transfers").
    private static readonly Regex ClearFiltersSignal = new(
        @"\b(?:include everything|all transactions|no filters?|remove (?:the )?filters?|without (?:any )?(?:exclusions|filters)|show (?:me )?everything|reset filters?|clear filters?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IncludeTransfersSignal = new(
        @"\b(?:include|includ(?:e|ing)|add|count(?:ing)?|keep)\s+(?:the\s+)?transfers?\b|\btransfers?\s+(?:back|included|too)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool WantsClearFilters(string message) => !string.IsNullOrWhiteSpace(message) && ClearFiltersSignal.IsMatch(message);

    internal static bool WantsIncludeTransfers(string message) =>
        !string.IsNullOrWhiteSpace(message) && (WantsClearFilters(message) || IncludeTransfersSignal.IsMatch(message));

    // Term(s) following an exclusion marker: "excluding rent", "without groceries", "except food".
    private static readonly Regex ExclusionTermSignal = new(
        @"\b(?:excluding|except(?:\s+for)?|without|not including|don'?t count|don'?t include|ignore|ignoring|other than)\s+(?<term>[\p{L}][\p{L}\p{N}\s&'-]{0,40}?)(?=\b(?:and|but|from|in|on|for|last|this|previous|current|per|each)\b|[?.!,]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "only <x>" / "just <x>" restrict scope to x -- but "just tell me" is a filler, not a
    // category, so a following verb/filler token is dropped.
    private static readonly Regex InclusionTermSignal = new(
        @"\b(?:only|just)\s+(?<term>[\p{L}][\p{L}\p{N}\s&'-]{0,40}?)(?=\b(?:and|but|from|in|on|for|last|this|previous|current|per|each)\b|[?.!,]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HypotheticalSignal = new(
        @"\b(what if|suppose|assuming|hypothetically|imagine|if i (?:save|saved|earn|earned|make|made|had|invest|invested|spend|spent|cut|increase|increased|reduce|reduced)|if my \w+ (?:increase|increases|goes up|drops|drop|rises|falls|changes))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "not asking about my wishlist", "nothing to do with recurring".
    private static readonly Regex NegatedTopicSignal = new(
        @"\b(?:not (?:asking|talking) about|nothing to do with|not about|forget about)\s+(?:my\s+|the\s+)?(?<term>[\p{L}][\p{L}\p{N}\s&'-]{0,40}?)(?=\b(?:and|but|just|only|please)\b|[?.!,]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Tokens that are verbs/fillers, never a real category/scope term.
    private static readonly HashSet<string> InclusionStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "tell", "show", "give", "list", "find", "get", "want", "need", "me", "us", "the", "a", "an",
        "my", "that", "this", "it", "them", "those", "these", "total", "totals", "amount", "sum",
        "now", "please", "answer", "number", "count"
    };

    internal static AiConstraints ParseConstraints(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return AiConstraints.None;

        var excludeTransfers = ExcludeTransfersSignal.IsMatch(message);
        var excluded = ExtractTerms(ExclusionTermSignal, message)
            .Where(term => !IsTransferTerm(term))
            .ToList();
        var included = ExtractTerms(InclusionTermSignal, message)
            .Where(term => !term.Split(' ').All(word => InclusionStopWords.Contains(word)))
            .Select(TrimFiller)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .ToList();
        var negatedTopics = ExtractTerms(NegatedTopicSignal, message);

        return new AiConstraints(
            PreventNavigation: PreventNavigationSignal.IsMatch(message),
            ExcludeTransfers: excludeTransfers,
            ExcludedCategories: excluded,
            IncludedCategories: included,
            NegatedTopics: negatedTopics,
            Hypothetical: HypotheticalSignal.IsMatch(message));
    }

    private static List<string> ExtractTerms(Regex signal, string message) =>
        signal.Matches(message)
            .Select(match => NormalizeSearchText(match.Groups["term"].Value.Trim()))
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsTransferTerm(string term) =>
        term.Equals("transfer", StringComparison.OrdinalIgnoreCase) ||
        term.Equals("transfers", StringComparison.OrdinalIgnoreCase);

    // Common trailing fillers ("please", "now") that ride along after a scope term.
    private static readonly HashSet<string> TrailingFiller = new(StringComparer.OrdinalIgnoreCase)
    {
        "please", "now", "thanks", "too", "instead"
    };

    private static string TrimFiller(string term)
    {
        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 0 && InclusionStopWords.Contains(words[0]))
        {
            words.RemoveAt(0);
        }
        while (words.Count > 0 && (InclusionStopWords.Contains(words[^1]) || TrailingFiller.Contains(words[^1])))
        {
            words.RemoveAt(words.Count - 1);
        }
        return string.Join(' ', words);
    }

    // Resolves free-text exclusion/inclusion terms against the user's real category and ledger
    // category names, so "excluding rent" only filters when "rent" is an actual category.
    internal static (IReadOnlyList<string> Categories, IReadOnlyList<string> LedgerCategories) ResolveConstraintCategories(
        IReadOnlyList<string> terms,
        IReadOnlyList<string> categories,
        IReadOnlyList<string> ledgerCategories)
    {
        var matchedCategories = new List<string>();
        var matchedLedger = new List<string>();
        foreach (var term in terms)
        {
            var category = categories.FirstOrDefault(c => c.Equals(term, StringComparison.OrdinalIgnoreCase))
                ?? categories.FirstOrDefault(c => term.Contains(c, StringComparison.OrdinalIgnoreCase) || c.Contains(term, StringComparison.OrdinalIgnoreCase));
            if (category != null && !matchedCategories.Contains(category)) matchedCategories.Add(category);

            var ledger = ledgerCategories.FirstOrDefault(c => c.Equals(term, StringComparison.OrdinalIgnoreCase));
            if (ledger != null && !matchedLedger.Contains(ledger)) matchedLedger.Add(ledger);
        }
        return (matchedCategories, matchedLedger);
    }
}
