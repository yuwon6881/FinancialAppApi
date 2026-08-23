using System.Globalization;
using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private static readonly Regex CycleKeyPattern = new(@"^\d{4}-(0[1-9]|1[0-2])$", RegexOptions.Compiled);

    private static bool IsValidCycleKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || !CycleKeyPattern.IsMatch(key)) return false;
        var year = int.Parse(key[..4], CultureInfo.InvariantCulture);
        return year is >= 1900 and <= 2100;
    }

    // "yyyy-MM" <-> CycleKey, the canonical wire form for a resolved cycle in the conversation
    // frame (unambiguous, unlike "this"/"last cycle").
    private static string FormatCycleKey(CycleKey cycle) =>
        $"{cycle.Year:D4}-{cycle.MonthIndex:D2}";

    private static CycleKey? ParseCycleKey(string? key) =>
        IsValidCycleKey(key)
            ? new CycleKey(int.Parse(key![..4], CultureInfo.InvariantCulture), int.Parse(key[5..], CultureInfo.InvariantCulture))
            : null;

    // Coarse income/inflow/outflow/transfer classification of a request, stored on the frame so a
    // follow-up keeps the same money-direction filter. Best-effort keyword match; null when the
    // request doesn't lean one way. Income is narrower than inflow: it means positive rows assigned
    // to the Income ledger, while inflow includes direct bucket credits such as reimbursements.
    private static string? DetectTransactionType(string queryText)
    {
        // Mentioning transfers in an exclusion ("spending without transfers") describes the
        // boundary, not the requested transaction type. Only a positive transfer request is typed
        // as transfer.
        if (!ExcludeTransfersSignal.IsMatch(queryText) &&
            Regex.IsMatch(queryText, @"\b(?:show|list|find|only|just|my|all)?\s*transfers?\b|\btransfer transactions?\b", RegexOptions.IgnoreCase))
            return "transfer";
        if (Regex.IsMatch(queryText, @"\b(income|incomes|earning|earnings|earned|salary|salaries|paycheck|paychecks|revenue)\b", RegexOptions.IgnoreCase)) return "income";
        if (Regex.IsMatch(queryText, @"\b(inflow|inflows|deposits?|received|credited?)\b", RegexOptions.IgnoreCase)) return "inflow";
        if (Regex.IsMatch(queryText, @"\b(outflow|outflows|expenses?|spending|spent|spend|withdrawals?|debited?)\b", RegexOptions.IgnoreCase)) return "outflow";
        return null;
    }

    private const string MonthNamePattern = "jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?";

    private static readonly Regex AllHistorySignal = new(
        @"\b(?:historical\s+(?:data|history|records?|transactions?)|all\s+(?:saved\s+)?history|full\s+history|all[- ]?time|(?:all|every|each)\s+(?:cycles?|months?)|(?:across|throughout|over)\s+all\s+(?:cycles?|months?|history)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CycleComparisonSignal = new(
        @"\b(compare|comparison|vs\.?|versus|trend|over time|each month|every month|past (few |\d+ )?months?|past (few |\d+ )?cycles?|year over year|month over month)\b",
        RegexOptions.Compiled);

    private static readonly Regex CycleAnalysisSignal = new(
        @"\b(spend|spent|spending|budget|income|earnings?|salary|paychecks?|cash ?flow|outflow|inflow|balance|total|average|net|save|saved|savings|essentials|growth|stability|rewards|cycle|this month|last month|how much|money going|doing better|doing worse|afford|financial health|performance|expense|expenses|cost|costs|fee|fees|profit|profits|margin|margins)\b",
        RegexOptions.Compiled);

    private static readonly Regex CategoryLimitSignal = new(
        @"\b(cat(?:egory)?\.?\s*(?:lim(?:it)?s?|caps?)|spend(?:ing)?\s*(?:lim(?:it)?s?|caps?)|" +
        @"budg(?:et)?\s*(?:lim(?:it)?s?|caps?)|category limits?|spending limits?|budget limits?|category caps?|spending caps?|budget caps?|" +
        @"limit for|limits? (?:did i|do i|have i|am i|was i|are|is)|" +
        @"over (?:my |the )?limit|under (?:my |the )?limit|within (?:my |the )?limit|" +
        @"exceed(?:ed|ing)? (?:my |the )?limit|remaining (?:for|in) [\p{L}\p{N}&' -]+ limit|" +
        @"(?:how(?:'s| is)|where(?:'s| is)|what(?:'s| is)|status|progress|check)\b.{0,35}\b(?:budget|cap|allowance)|" +
        @"(?:budget|cap|allowance)\b.{0,20}\b(?:left|remaining|available|status|progress|room)|" +
        @"(?:room|amount|money)\s+(?:left|remaining|available)\b.{0,30}\b(?:budget|cap|allowance)|" +
        @"(?:what|which|show|tell me|list|check|view|see)\b.{0,40}\b" +
        @"(?:my |current |currently configured |configured |set )?(?:category |spending |budget )?(?:limit|limits|caps?))\b|" +
        @"^\s*(?:my\s+|current\s+)?(?:cat(?:egory)?\.?\s+|spend(?:ing)?\s+|budg(?:et)?\s+)?(?:lim(?:it)?s?|caps?|allowances?)\s*[?!.]*\s*$|" +
        @"^\s*[\p{L}\p{N}&'-]+(?:\s+[\p{L}\p{N}&'-]+){0,2}\s+(?:budget|cap|allowance)\s*[?!.]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CycleInsightSignal = new(
        @"\b(cycle summary|cycle recap|cycle overview|cycle review|" +
        @"cycle update|month update|cycle health|month health|ongoing cycle|" +
        @"summary|recap|overview|review|progress|status)\s+(?:for|of|on)\s+(?:this|last|previous|current) (?:cycle|month)\b|" +
        @"\b(?:summari[sz]e|recap|review)\s+(?:this|last|previous|current) (?:cycle|month)\b|" +
        @"\b(?:this|last|previous|current) (?:cycle|month)\b.{0,30}\b" +
        @"(?:so far|to date|month to date|summary|recap|overview|review|progress|status|update|health|going|looking|tracking)\b|" +
        @"\b(?:how am i doing|how am i tracking|how are things|how is it going|how's it going|" +
        @"where do i stand|what has happened|what happened)\b(?:.{0,30}\b(?:cycle|month|financially)\b)?|" +
        @"\b(?:cycle|month)\s+(?:so far|to date|update|health|check)\b|" +
        @"\b(?:month|cycle)[- ]to[- ]date\b|" +
        @"\b(average daily spend|daily spending average|spending velocity|first half|second half|" +
        @"no[- ]spend days?|committed spend|discretionary spend|biggest spending day)\b|" +
        @"^\s*(?:(?:this|last|previous|current)\s+(?:cycle|month)|(?:cycle\s+)?" +
        @"(?:summary|recap|overview|review|progress|status|update|health)|(?:so far|month to date|" +
        @"where do i stand|how am i tracking))\s*[?!.]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TransactionDetailSignal = new(
        @"\b(transaction|transactions|ledger|purchase|purchased|bought|paid|payment|receipt|charge|charged|expense|expenses|deposit|deposits|withdrawal|withdrawals|refund|refunds|debit|debits|credit|credits|find|search|when did|did i|edit|update|change|modify|delete|remove|erase|export|download|record|entry|merchant|cost me|how often|how frequently|frequency|largest|biggest|highest|lowest|smallest|most expensive|cheapest|invoice|invoices|bill|bills|fee|fees|cost|costs|priced|billed)\b",
        RegexOptions.Compiled);

    private static bool LooksLikeLedgerDraftList(string message) => CountLedgerDraftListRecords(message) > 0;

    // People do not always type one record per line. "Transfer 50 from CIMB to RYT, and spent 53
    // at Grab Mart" is two records in one sentence, and reading it as one unparseable line is how
    // a perfectly clear instruction became "send it as a description and an amount on one line".
    private static readonly Regex DraftSegmentSeparator = new(
        @"\s*(?:[,;]|\band\b)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DraftAmountToken = new(
        @"(?<![\p{L}\p{N}.])(?:rm|myr|usd|sgd|eur|gbp|aud|cad|jpy|cny|rmb|\$|€|£)\s*\d{1,9}(?:[.,]\d{1,2})?" +
        @"|(?<![\p{L}\p{N}.])\d{1,9}(?:[.,]\d{1,2})?(?![\p{L}\p{N}.])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A segment that is not in strict "description amount" shape still records money when it names
    // the movement plainly. Requiring the verb keeps a question or a bare noun phrase out.
    private static readonly Regex DraftRecordVerb = new(
        @"\b(spent|spend|paid|pay|bought|buy|purchased|transfer(?:red)?|move[ds]?|moving|sent|send|" +
        @"received|receive|got|deposit(?:ed)?|withdrew|withdrawn|top(?:ped)? ?up|refund(?:ed)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StrictDraftLine = new(
        @"^[\p{L}\p{N}][\p{L}\p{N}&'().,+/ -]{0,159}?\s+" +
        @"(?:(?:rm|myr|usd|sgd|eur|gbp|aud|cad|jpy|cny|rmb|\$|€|£)\s*)?" +
        // A single record is often typed as a sum of its parts ("Mamak 18+2.30" -- the meal plus
        // the drink). That is still one record, so it must still pin the response to one action.
        @"\d{1,9}(?:[.,]\d{1,2})?(?:\s*\+\s*\d{1,9}(?:[.,]\d{1,2})?)*" +
        @"(?:\s+(?:income|inflow|outflow|expense|refund|deposit|withdrawal|" +
        @"transfer(?:\s+from\s+[\p{L}]+\s+to\s+[\p{L}]+)?|" +
        @"essentials?|growth|stability|rewards?|[\p{L}][\p{L}-]{0,30})){0,3}\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Split only when every piece carries its own amount -- otherwise "Nasi Lemak and Teh 12"
    // would be torn into a nameless half. The whole line is not tested for validity first: a
    // three-record sentence can satisfy the single-record shape by accident, since a comma is a
    // legal description character and only the last amount has to sit near the end.
    private static IReadOnlyList<string> SplitDraftSegments(string line)
    {
        var parts = DraftSegmentSeparator.Split(line)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
        if (parts.Count < 2) return [line];
        return parts.All(part => DraftAmountToken.Matches(part).Count == 1) ? parts : [line];
    }

    internal static int CountLedgerDraftListRecords(string message)
    {
        var lines = message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .SelectMany(SplitDraftSegments)
            .ToList();
        if (lines.Count is < 1 or > AiResponseSchemas.MaxChatActions) return 0;
        if (lines.Any(line =>
                line.Contains('?') ||
                Regex.IsMatch(line, @"^(?:how|what|which|when|where|why|who|can|could|would|should|did|do|does|is|are|show|find|list)\b",
                    RegexOptions.IgnoreCase)))
        {
            return 0;
        }

        var everyLineIsADraft = lines.All(line => StrictDraftLine.IsMatch(line) ||
            (DraftAmountToken.Matches(line).Count == 1 && DraftRecordVerb.IsMatch(line)));
        return everyLineIsADraft ? lines.Count : 0;
    }

    // "how much / how many / total / average" questions are answered from the cycle summary
    // aggregates, so they never need the per-row detail block -- even though a phrase like
    // "how much did I spend" trips TransactionDetailSignal on the incidental "did i".
    private static readonly Regex AggregateQuestionSignal = new(
        @"\b(how much|how many|total|totals|average|averages|avg|breakdown|sum)\b",
        RegexOptions.Compiled);

    // A cadence question is not only about things that are bought. Services -- a haircut, a car
    // wash, a dentist visit -- are "done", "had", "performed" or "gone for", and the old
    // buy/purchase/replace/restock list left "how often do I perform a hair cut" routed as an
    // ordinary cycle question: scoped to the loaded cycle instead of all saved history, with no
    // cadence metric at all. Any "how often / frequency" wording paired with an action verb is a
    // cadence question; the metric itself still reports honestly when nothing matches.
    private const string CadenceVerbPattern =
        @"buy|buys|buying|bought|purchase[sd]?|purchasing|get|gets|getting|got|replace[sd]?|replacing|" +
        @"restock(?:s|ed|ing)?|reorder(?:s|ed|ing)?|order(?:s|ed|ing)?|refill(?:s|ed|ing)?|top\s?up|top(?:ped|ping)?\s+up|" +
        @"renew(?:s|ed|ing)?|book(?:s|ed|ing)?|visit(?:s|ed|ing)?|go|goes|going|went|do|does|did|doing|done|" +
        @"perform(?:s|ed|ing)?|have|has|had|having|use[sd]?|using|pay|pays|paid|paying|spend|spends|spent|" +
        @"cut|cuts|cutting|service[sd]?|servicing|eat|eats|ate|eating|drink|drinks|drank|drinking|" +
        @"fill|fills|filled|filling|wash|washes|washed|washing|charge[sd]?|charging|clean(?:s|ed|ing)?";

    private static readonly Regex PurchaseFrequencySignal = new(
        $@"\b(?:how\s+(?:often|frequently)|frequency)\b[^?!.]{{0,80}}\b(?:{CadenceVerbPattern})\b|" +
        $@"\b(?:{CadenceVerbPattern})\b[^?!.]{{0,60}}\bhow\s+(?:often|frequently)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CountQuestionSignal = new(
        @"\b(how many|number of times|times did i|played|visited)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ...unless the user explicitly asks to see the individual records. These words mean the
    // detail sample is genuinely wanted and override the aggregate suppression above.
    private static readonly Regex ExplicitRecordSignal = new(
        @"\b(which|show|list|find|search|each|when did|edit|update|change|modify|delete|remove|erase|export|download|receipt|merchant|entry|entries)\b",
        RegexOptions.Compiled);

    private static readonly Regex RecurringSignal = new(
        @"\b(recurring|subscription|subscriptions|sub|subs|membership|memberships|renewal|renewals|renews?|bill|bills|instalments?|installments?|standing orders?|monthly payment|yearly payment|annual payment|autopay|auto-pay|auto-renewal|auto renewal|direct debit|direct debits|payment reminders?|bill reminders?|subscription reminders?|push reminders?)\b",
        RegexOptions.Compiled);

    private static readonly Regex LoanSignal = new(
        @"\b(loans?|mortgages?|financing|amount still owed|payoff date|pay off|interest remaining)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WishlistSignal = new(
        @"\b(wishlist|wish list|wish-list|wish|bucket list|dream purchase|next purchase|want to buy|planning to buy|saving for|save for|saving up|save up|priority item|afford|goal|goals|savings? goal|savings? target)\b",
        RegexOptions.Compiled);

    private static readonly Regex ExplicitWishlistSignal = new(
        @"\b(wishlist|wish list|wish-list|dream purchase|next purchase|want to buy|planning to buy|priority item)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WishlistForecastSignal = new(
        @"\b(how long|when can i|when could i|when will i|when would i|reach|hit|achieve|afford|target date|months? until|cycles? until|how many months|time to save)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Coaching/advice intent -- "how do I improve", "where can I cut", "am I on track".
    // Grounding this kind of answer needs the user's allocation targets, not just actuals,
    // so it turns on the budgetTargets block (and cycle summaries) the same way analysis does.
    private static readonly Regex ImprovementSignal = new(
        @"\b(improve|improving|reduce|reducing|cut|cutting|spend less|save more|advice|advise|suggest|suggestion|recommend|recommendation|on track|over ?budget|under ?budget|overspend|overspending|should i|where can i|too much|tips?|optimi[sz]e|budgeting|plan|planning|goal|goals)\b",
        RegexOptions.Compiled);

    private static readonly Regex InvestmentCoreSignal = new(
        @"\b(portfolio|holding|holdings|investment(?:s)?|broker|instrument|on paper|already banked|dividend|dividends)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal sealed record IntentClassification(
        IReadOnlyList<string> Intents,
        double Confidence,
        string? SearchText,
        string? CycleHint,
        DateOnly? Date = null,
        string? Category = null,
        string? LedgerCategory = null,
        decimal? Amount = null,
        string? WishlistReference = null,
        string? TransactionReference = null,
        AiConstraints? Constraints = null,
        IReadOnlyList<string>? Ambiguities = null,
        string? LedgerAccountReference = null);

    // Phase 3: classifier output is untrusted model text -- validate in application code, never
    // rely solely on provider-side schema enforcement. Rejects unknown intents, clamps
    // confidence, normalizes/limits search text. Any IDs the classifier invents are discarded
    // (never parsed here) -- records are only ever resolved against the DB.
    internal static IntentClassification? SanitizeClassification(
        IReadOnlyList<string>? rawIntents,
        double rawConfidence,
        string? searchText,
        string? cycleHint,
        DateOnly? date = null,
        string? category = null,
        string? ledgerCategory = null,
        decimal? amount = null,
        string? wishlistReference = null,
        string? transactionReference = null,
        AiConstraints? constraints = null,
        IReadOnlyList<string>? ambiguities = null,
        string? ledgerAccountReference = null)
    {
        var intents = (rawIntents ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i.Trim().ToLowerInvariant())
            .Where(IsKnownIntentName)
            .Distinct()
            .Take(4)
            .ToList();
        if (intents.Count == 0) return null;

        var confidence = double.IsFinite(rawConfidence) ? Math.Clamp(rawConfidence, 0d, 1d) : 0d;
        string? Clean(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var normalized = System.Text.RegularExpressions.Regex.Replace(value.Trim(), @"\s+", " ");
            return normalized.Length > MaxStateSearchLength ? normalized[..MaxStateSearchLength] : normalized;
        }
        DateOnly? validDate = date;
        if (validDate.HasValue && (validDate.Value < new DateOnly(1900, 1, 1) || validDate.Value > new DateOnly(2100, 12, 31))) validDate = null;
        decimal? validAmount = amount is >= 0m and <= 1_000_000_000m ? amount : null;
        var cleanAmbiguities = (ambiguities ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(Clean)
            .Where(a => a != null)
            .Cast<string>()
            .Take(8)
            .ToList();
        var cleanLedgerCategory = Clean(ledgerCategory);
        if (cleanLedgerCategory != null && !LedgerCategories.Contains(cleanLedgerCategory, StringComparer.OrdinalIgnoreCase))
        {
            cleanLedgerCategory = null;
        }
        return new IntentClassification(
            intents, confidence, Clean(searchText), Clean(cycleHint), validDate,
            Clean(category), cleanLedgerCategory, validAmount,
            Clean(wishlistReference), Clean(transactionReference), constraints, cleanAmbiguities,
            Clean(ledgerAccountReference));
    }

    internal enum TransactionDataLevel
    {
        None,
        AggregateOnly,
        MatchingRows,
        BoundedSample
    }

    internal enum DerivedMetric
    {
        ActivityCount,
        PurchaseCadence,
        MerchantMatches,
        AnomalyDetection,
        DuplicateDetection,
        CycleTotals,
        CycleComparison,
        WishlistForecast,
        AllocationPerformance,
        RecurringUpcoming,
        DailyExtremes,
        BalanceSnapshot
    }
}
