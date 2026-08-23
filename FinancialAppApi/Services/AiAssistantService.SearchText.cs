using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private static string? ExtractLikelySearchText(string message, IReadOnlyList<string> intents)
    {
        if (!intents.Any(i => i.Equals("ledger.activity_count", StringComparison.OrdinalIgnoreCase) ||
                              i.Equals("ledger.purchase_frequency", StringComparison.OrdinalIgnoreCase) ||
                              i.Equals("ledger.merchant_search", StringComparison.OrdinalIgnoreCase) ||
                              i.Equals("ledger.spending_total", StringComparison.OrdinalIgnoreCase) ||
                              i.Equals("ledger.transaction_list", StringComparison.OrdinalIgnoreCase))) return null;

        // The action verb is optional here so a service phrased as its own verb still yields a
        // term ("how often do i haircut" -> "haircut"), and the article/preposition run after it
        // is consumed so "how often do i go for a hair cut" searches for "hair cut" rather than
        // "for a hair cut", which matches nothing.
        var purchaseFrequency = Regex.Match(message,
            $@"\b(?:(?:how\s+(?:often|frequently))|(?:(?:approximately\s+)?(?:what(?:'s|\s+is)|whats)\s+(?:the\s+)?frequency)|frequency)\s+(?:do|did|does|have|has|am|are|is|would|will)?\s*(?:i|we)?\s*(?:usually\s+|typically\s+|normally\s+|generally\s+)?(?:(?:{CadenceVerbPattern})\s+)?(?:(?:to|for|at|on|a|an|the|my|some)\s+)*(?<value>[\p{{L}}\p{{N}}][\p{{L}}\p{{N}}'& -]{{0,60}}?)(?=\s+\b(?:last|this|previous|current|past|in|during|across|over|throughout)\b|[?.!,]|$)",
            RegexOptions.IgnoreCase);
        if (purchaseFrequency.Success) return NormalizeSearchText(purchaseFrequency.Groups["value"].Value);

        // Common natural-language shapes. Keep the captured term deliberately short and stop
        // before cycle wording so "TNG transactions in the last 3 cycles" searches for TNG,
        // not for the whole tail of the sentence.
        var genericTransactions = Regex.Match(message,
            @"\b(?:any|show|find|list|search(?:\s+for)?)\s+(?<value>[\p{L}\p{N}][\p{L}\p{N}'& -]{0,60}?)\s+(?:transactions?|payments?|purchases?|charges?|records?|entries)\b",
            RegexOptions.IgnoreCase);
        if (genericTransactions.Success) return NormalizeSearchText(genericTransactions.Groups["value"].Value);

        var spendOn = Regex.Match(message,
            @"\b(?:spend|spent|spending|paid|pay|cost)\s+(?:how much\s+)?(?:on|at|for|to)\s+(?<value>[\p{L}\p{N}][\p{L}\p{N}'& -]{0,60}?)(?=\s+\b(?:last|this|previous|current|past|in|during|across)\b|[?.!,]|$)",
            RegexOptions.IgnoreCase);
        if (spendOn.Success) return NormalizeSearchText(spendOn.Groups["value"].Value);

        // Count questions commonly put a user-supplied subject directly before the record
        // noun. Capture that subject independently of the surrounding sentence structure.
        var countRecords = Regex.Match(message,
            @"\b(?:how many|number of)\s+(?<value>[\p{L}\p{N}][\p{L}\p{N}'& -]{0,60}?)(?:[-\s]+related)?\s+(?:transactions?|payments?|purchases?|charges?|records?|entries)\b",
            RegexOptions.IgnoreCase);
        if (countRecords.Success) return NormalizeSearchText(countRecords.Groups["value"].Value);

        // Also support inverted forms where the record noun precedes the dynamic subject.
        var recordsForSubject = Regex.Match(message,
            @"\b(?:how many|number of)\s+(?:transactions?|payments?|purchases?|charges?|records?|entries)\s+(?:for|about|related\s+to|matching)\s+(?<value>[\p{L}\p{N}][\p{L}\p{N}'& -]{0,60}?)(?=\s+\b(?:last|this|previous|current|past|in|during|across)\b|[?.!,]|$)",
            RegexOptions.IgnoreCase);
        if (recordsForSubject.Success) return NormalizeSearchText(recordsForSubject.Groups["value"].Value);

        var countMatch = Regex.Match(message,
            @"\b(?:how many|how often|number of times)\s+(?<value>[\p{L}\p{N}][\p{L}\p{N}'& -]{1,50}?)\s+(?:did|do|does|have|has|i|we)\b",
            RegexOptions.IgnoreCase);
        if (countMatch.Success) return NormalizeSearchText(countMatch.Groups["value"].Value);

        var showMatch = Regex.Match(message,
            @"\b(?:show|find|search|latest|last)\s+(?<value>[\p{L}\p{N}][\p{L}\p{N}'& -]{1,50}?)\s+(?:spending|purchase|purchases|transactions?|payments?)\b",
            RegexOptions.IgnoreCase);
        if (showMatch.Success) return NormalizeSearchText(showMatch.Groups["value"].Value);

        if (!intents.Any(i => i.Equals("ledger.activity_count", StringComparison.OrdinalIgnoreCase) ||
                              i.Equals("ledger.merchant_search", StringComparison.OrdinalIgnoreCase) ||
                              i.Equals("ledger.spending_total", StringComparison.OrdinalIgnoreCase))) return null;

        var merchantMatch = Regex.Match(message,
            @"\b(?:at|from|for|about|with)\s+(?<value>[\p{L}\p{N}][\p{L}\p{N}'& -]{1,60}?)(?:\s+(?:last|this|previous|current|in|on)\b|[?.!,]|$)",
            RegexOptions.IgnoreCase);
        return merchantMatch.Success ? NormalizeSearchText(merchantMatch.Groups["value"].Value) : null;
    }

    // Bare pronouns/verbs that the loose "how many X did I" pattern can accidentally capture
    // as the subject when the message has no real subject ("how many did I do?").
    private static readonly HashSet<string> SearchNoiseTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "did", "do", "does", "have", "has", "i", "we", "it", "that", "this", "them", "those", "these", "the", "a", "an",
        // Request scaffolding that only ever surfaces as residue after an analysis word is stripped.
        "any", "some", "me", "my", "show", "find", "list", "search", "all", "there", "other", "same"
    };

    private static bool IsNoiseSearchTerm(string? term) =>
        !string.IsNullOrWhiteSpace(term) && SearchNoiseTerms.Contains(term.Trim());

    // Cycle/amount wording is never a merchant/activity search. A follow-up like "how about last
    // cycle over 100" would otherwise let the "about" preposition capture "last cycle over 100" as
    // a bogus search filter (matching nothing). Genuine inherited search comes from the prior
    // frame, not from re-extracting the expanded text.
    private static readonly Regex NonSearchPhraseSignal = new(
        @"\b(cycle|cycles|month|months|year|years|over|under|above|below|between|exceed(?:s|ed|ing)?|more than|less than|at least|at most)\b|^(last|this|previous|current|next|prior)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool LooksLikeCycleOrAmountPhrase(string? term) =>
        !string.IsNullOrWhiteSpace(term) && NonSearchPhraseSignal.IsMatch(term);

    // Analysis vocabulary names the KIND of analysis being asked for, never a merchant. The
    // "any X transactions" shape captures it as a search term ("any duplicate transactions this
    // cycle" -> "duplicate"), which then filters the cycle down to descriptions containing that
    // word -- matching nothing -- so the assistant reported an empty cycle over a full ledger.
    private static readonly Regex AnalysisVocabularySignal = new(
        @"\b(?:duplicates?|duplicated|duplicate[ds]|dupes?|repeats?|repeated|repeating|doubles?|doubled|charged|twice|unusual|odd|strange|weird|anomal(?:y|ies|ous)|outliers?|suspicious|abnormal|irregular)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Removes analysis words from an extracted term, keeping any real subject that remains
    // ("duplicate Digi" -> "Digi") and dropping the term entirely when nothing else is left.
    private static string? StripAnalysisVocabulary(string? term)
    {
        if (string.IsNullOrWhiteSpace(term)) return term;
        var remainder = AnalysisVocabularySignal.Replace(term, " ");
        if (remainder == term) return term;
        // What surrounds an analysis word is usually the request's own scaffolding ("show me any
        // duplicate transactions"), so drop bare noise tokens from the residue as well -- keeping
        // them would reinstate exactly the match-nothing filter this strip exists to remove.
        var kept = remainder
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !SearchNoiseTerms.Contains(token))
            .ToArray();
        return kept.Length == 0 ? null : string.Join(' ', kept);
    }

    private static string NormalizeSearchText(string value)
    {
        var normalized = Regex.Replace(value.Trim(), @"^(?:my|the)\s+", string.Empty, RegexOptions.IgnoreCase);
        // The count extractor can capture the whole noun phrase before the user's pronoun,
        // but a trailing record type is request scaffolding rather than part of the dynamic
        // subject. Remove only that generic scaffolding before querying the ledger.
        return Regex.Replace(
            normalized,
            @"(?:[-\s]+related)?\s+(?:(?:outflow|inflow|expense|spending)\s+)?(?:transactions?|payments?|purchases?|charges?|records?|entries)\s*$",
            string.Empty,
            RegexOptions.IgnoreCase).Trim();
    }
}
