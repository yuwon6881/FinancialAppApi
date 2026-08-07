using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private async Task<IntentClassification?> TryClassifyIntentAsync(
        string message,
        AiConversationState? priorState,
        CancellationToken cancellationToken)
    {
        // No prior dialogue is sent -- only a compact, canonical summary of the prior turn's
        // resolved frame (a few tokens) so the classifier can still interpret a short follow-up
        // ("how about last cycle") without shipping the previous message text back to the model.
        var classifierPrompt = $"Classify the user's financial-app request. Return only the JSON schema. " +
            $"Choose one or more intents, extract searchText for a merchant/activity, and preserve cycle wording. " +
            $"Treat shorthand, omitted nouns, abbreviations, and conversational equivalents by meaning: " +
            $"category_limits.analysis covers category budgets/caps/allowances and remaining room; " +
            $"cycle.insights covers a cycle/month recap, progress, health, update, 'so far', 'how am I tracking', " +
            $"or 'where do I stand', including an ongoing cycle that has no saved end-of-cycle summary. " +
            $"User message: {JsonSerializer.Serialize(message)} " +
            $"Prior request summary: {JsonSerializer.Serialize(SummarizePriorFrame(priorState))}";
        try
        {
            var text = await _aiClient.GenerateTextAsync(
                [AiPart.FromText(classifierPrompt)],
                new AiGenerationOptions(
                    Feature: "chat-intent-classification",
                    Temperature: 0,
                    MaxOutputTokens: 220,
                    OutputJsonSchema: AiResponseSchemas.IntentClassification,
                    ThinkingLevel: "none",
                    ModelConfigurationKey: "OpenAiModels:IntentClassifier"),
                cancellationToken);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var intents = root.TryGetProperty("intents", out var intentsElement) && intentsElement.ValueKind == JsonValueKind.Array
                ? intentsElement.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList()
                : [];
            var confidence = root.TryGetProperty("confidence", out var confidenceElement) && confidenceElement.TryGetDouble(out var value) ? value : 0;
            // searchText/cycleHint may arrive top-level (legacy) or under an "entities" object.
            var entities = root.TryGetProperty("entities", out var entitiesElement) && entitiesElement.ValueKind == JsonValueKind.Object
                ? entitiesElement
                : root;
            string? StringField(JsonElement parent, string name) =>
                parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
            var searchText = StringField(entities, "searchText") ?? StringField(root, "searchText");
            var cycleHint = StringField(entities, "cycleHint") ?? StringField(root, "cycleHint")
                ?? StringField(entities, "cycleReference") ?? StringField(entities, "cycleHint");
            var date = StringField(entities, "date") is { } dateText &&
                DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
                ? parsedDate : (DateOnly?)null;
            var category = StringField(entities, "category");
            var ledgerCategory = StringField(entities, "ledgerCategory");
            decimal? amount = null;
            if (entities.TryGetProperty("amount", out var amountElement) && amountElement.ValueKind == JsonValueKind.Number &&
                amountElement.TryGetDecimal(out var parsedAmount) && parsedAmount >= 0 && parsedAmount <= 1_000_000_000m)
            {
                amount = parsedAmount;
            }
            var wishlistReference = StringField(entities, "wishlistReference");
            var transactionReference = StringField(entities, "transactionReference");
            var classifierConstraints = ParseClassifierConstraints(root);
            var ambiguities = root.TryGetProperty("ambiguities", out var ambiguityElement) && ambiguityElement.ValueKind == JsonValueKind.Array
                ? ambiguityElement.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : [];
            // SanitizeClassification rejects unknown intents, clamps confidence, and normalizes text.
            return SanitizeClassification(intents, confidence, searchText, cycleHint, date, category, ledgerCategory,
                amount, wishlistReference, transactionReference, classifierConstraints, ambiguities);
        }
        catch (Exception ex) when (ex is AiClientException or JsonException or FormatException)
        {
            return null;
        }
    }

    private static AiConstraints? ParseClassifierConstraints(JsonElement root)
    {
        if (!root.TryGetProperty("constraints", out var element) || element.ValueKind != JsonValueKind.Object) return null;
        bool Bool(string name) => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();
        var exclusions = element.TryGetProperty("exclusions", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)).Take(20).ToList()
            : [];
        return new AiConstraints(Bool("preventNavigation"), Bool("excludeTransfers"), exclusions, [], [], Bool("hypothetical"));
    }

    private static string? ExtractLikelySearchText(string message, IReadOnlyList<string> intents)
    {
        if (!intents.Any(i => i.Equals("ledger.activity_count", StringComparison.OrdinalIgnoreCase) ||
                              i.Equals("ledger.merchant_search", StringComparison.OrdinalIgnoreCase) ||
                              i.Equals("ledger.spending_total", StringComparison.OrdinalIgnoreCase) ||
                              i.Equals("ledger.transaction_list", StringComparison.OrdinalIgnoreCase))) return null;

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

    // Builds the base conversation frame for this turn: this turn's resolved intent/search/cycle,
    // with every other dimension carried forward from the prior frame (BuildContextAsync then
    // overrides the dimensions actually in play this turn with their freshly-resolved values). No
    // longer reads the prior message text -- a referential follow-up ("which of those") is detected
    // from the CURRENT message via UsesPriorTransactionState, and the prior frame supplies the ids.
    private static AiConversationState ResolveConversationState(
        string message,
        IReadOnlyList<string> intents,
        string? searchText,
        string? cycleHint,
        AiConversationState? priorState = null)
    {
        var carriesTransactionState = UsesPriorTransactionState(message);
        var resolvedIntents = intents.Where(i => !i.Equals("general", StringComparison.OrdinalIgnoreCase)).ToList();
        var topic = DetermineConversationTopic(message, resolvedIntents, priorState) ?? priorState?.LastTopic;
        var continuation = priorState != null && NeedsHistoryContext(message);
        // A self-contained question in a family starts a new frame for that family. A switch to a
        // different family may keep the dormant transaction frame so a later explicit cycle-only
        // continuation can return to it (the existing transaction -> wishlist -> cycle behavior).
        var carryTransactionFrame = priorState != null && (continuation || topic != TransactionTopic);
        var carryWishlistFrame = priorState != null && (continuation || topic != WishlistTopic);
        var carryRecurringFrame = priorState != null && (continuation || topic != RecurringTopic);
        return new AiConversationState(
            intents.FirstOrDefault(i => !i.Equals("general", StringComparison.OrdinalIgnoreCase)) ?? priorState?.LastIntent,
            searchText ?? (carryTransactionFrame ? priorState?.LastSearchText : null),
            cycleHint ?? (carryTransactionFrame ? priorState?.LastCycleHint : null),
            ExtractWishlistReference(message) ?? (carryWishlistFrame ? priorState?.LastWishlistReference : null),
            carryTransactionFrame ? priorState?.LastResolvedCycle : null,
            carryTransactionFrame && carriesTransactionState ? priorState?.LastMatchedTransactionIds : null,
            carryWishlistFrame && carriesTransactionState ? priorState?.LastWishlistItemId : null,
            carryTransactionFrame ? priorState?.LastCategory : null,
            carryTransactionFrame ? priorState?.LastResolvedCycleKeys : null,
            carryTransactionFrame ? priorState?.LastAmountThreshold : null,
            carryTransactionFrame && priorState?.LastExcludeTransfers == true,
            carryTransactionFrame ? priorState?.LastExcludedCategories : null,
            carryTransactionFrame ? priorState?.LastIncludedCategories : null,
            carryTransactionFrame ? priorState?.LastLedgerCategory : null,
            carryTransactionFrame ? priorState?.LastTransactionType : null,
            MessageMentionsCycle(message) && !MessageMentionsExactDate(message)
                ? null
                : carryTransactionFrame ? priorState?.LastExactDate : null,
            carryTransactionFrame && priorState?.LastComparison == true,
            carryRecurringFrame ? priorState?.LastRecurringReference : null,
            // This turn's resolved intents become the frame's intent set (BuildContextAsync leaves
            // this as-is); a non-general set here is what a later follow-up inherits its analysis
            // from.
            resolvedIntents.Count > 0 ? resolvedIntents : priorState?.LastIntents,
            topic,
            continuation ? priorState?.LastQueryFacets : null,
            carryRecurringFrame ? priorState?.LastRecurringStatus : null,
            carryWishlistFrame ? priorState?.LastWishlistStatus : null,
            carryTransactionFrame ? priorState?.LastTargetAmount : null);
    }

    private static string? ExtractConversationCycle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, @"\b((?:last|previous|prior|this|current)\s+(?:\d+\s+|few\s+)?(?:cycle|cycles|month|months))\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractWishlistReference(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, @"\b(?:wishlist|wish list|saving for|afford)\s+([\p{L}\p{N}][\p{L}\p{N}'& -]{1,60})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    // The `history` array is client-supplied on every request (this endpoint is stateless),
    // so it must not be trusted as-is: an unbounded role string or message length would let a
    // caller smuggle fabricated "instructions" into what the model is told is prior
    // conversation. Restrict to the two real roles and cap length like any other input.
    private static IReadOnlyList<AiChatMessage> SanitizeHistory(IReadOnlyList<AiChatMessage>? history)
    {
        if (history == null || history.Count == 0)
        {
            return [];
        }

        return history
            .TakeLast(12)
            .Where(m => m.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => m with { Content = m.Content.Length > MaxHistoryMessageLength ? m.Content[..MaxHistoryMessageLength] : m.Content })
            .ToList();
    }

    // The canonical intent vocabulary. Also used to reject unknown/tampered intent strings
    // arriving on client-carried conversation state or classifier output.
    private static readonly HashSet<string> KnownIntents = new(StringComparer.OrdinalIgnoreCase)
    {
        "ledger.activity_count", "ledger.merchant_search", "ledger.spending_total",
        "ledger.transaction_list", "ledger.comparison", "ledger.edit", "ledger.add",
        "ledger.anomaly", "ledger.duplicates", "wishlist.list", "wishlist.forecast",
        "wishlist.add", "wishlist.edit", "recurring.list", "recurring.upcoming",
        "recurring.add", "recurring.edit", "category_limits.analysis", "cycle.insights",
        "allocation.balance", "allocation.performance",
        "navigation", "general"
    };

    private const int MaxStateSearchLength = 80;
    private const int MaxStateMatchedIds = 50;

    // Client-carried conversation state is untrusted input, exactly like history. An unknown
    // intent, an over-long search string, or a giant id list must never flow into query
    // building unchecked. IDs here are only *hints*; they are re-derived/re-validated against
    // the DB before being surfaced again, never trusted verbatim.
    internal static AiConversationState? SanitizeConversationState(AiConversationState? state)
    {
        if (state == null) return null;
        var intent = !string.IsNullOrWhiteSpace(state.LastIntent) && KnownIntents.Contains(state.LastIntent)
            ? state.LastIntent.ToLowerInvariant()
            : null;
        string? Clamp(string? value) => string.IsNullOrWhiteSpace(value)
            ? null
            : (value.Length > MaxStateSearchLength ? value[..MaxStateSearchLength] : value).Trim();
        var matchedIds = state.LastMatchedTransactionIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Length > 64 ? id[..64] : id)
            .Take(MaxStateMatchedIds)
            .ToList();
        // Resolved-frame fields are untrusted too. Cycle keys must be canonical "yyyy-MM" in a sane
        // range; the threshold must round-trip through the canonical parser (rejecting anything the
        // parser can't read); excluded categories are clamped/capped; the ledger category must be a
        // real one.
        var cycleKeys = state.LastResolvedCycleKeys?
            .Where(IsValidCycleKey)
            .Distinct()
            .Take(24)
            .ToList();
        // Round-trip the typed threshold through the internal validator (rejects a bad comparator
        // name or out-of-range amount).
        var threshold = ToInternalThreshold(state.LastAmountThreshold) is { } internalThreshold
            ? ToWireThreshold(internalThreshold)
            : null;
        IReadOnlyList<string>? CleanCategoryList(IReadOnlyList<string>? list) => list?
            .Select(Clamp)
            .Where(c => c != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList() is { Count: > 0 } cleaned ? cleaned : null;
        var excludedCategories = CleanCategoryList(state.LastExcludedCategories);
        var includedCategories = CleanCategoryList(state.LastIncludedCategories);
        var ledgerCategory = !string.IsNullOrWhiteSpace(state.LastLedgerCategory)
            && LedgerCategories.Contains(state.LastLedgerCategory, StringComparer.OrdinalIgnoreCase)
            ? LedgerCategories.First(c => c.Equals(state.LastLedgerCategory, StringComparison.OrdinalIgnoreCase))
            : null;
        var transactionType = state.LastTransactionType is "inflow" or "outflow" or "transfer"
            ? state.LastTransactionType
            : null;
        var exactDate = DateOnly.TryParseExact(state.LastExactDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? state.LastExactDate
            : null;
        var intents = state.LastIntents?
            .Where(i => !string.IsNullOrWhiteSpace(i) && KnownIntents.Contains(i))
            .Select(i => i.ToLowerInvariant())
            .Distinct()
            .Take(6)
            .ToList();
        var topic = !string.IsNullOrWhiteSpace(state.LastTopic) && KnownConversationTopics.Contains(state.LastTopic)
            ? state.LastTopic
            : null;
        var facets = state.LastQueryFacets?
            .Where(f => !string.IsNullOrWhiteSpace(f) && KnownQueryFacets.Contains(f))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToList();
        var recurringStatus = state.LastRecurringStatus is "discarded" or "pending" or "paid" or "inactive" or "active"
            ? state.LastRecurringStatus
            : null;
        var wishlistStatus = state.LastWishlistStatus is "unpurchased" or "purchased" or "affordable" or "inactive" or "active"
            ? state.LastWishlistStatus
            : null;
        var targetAmount = state.LastTargetAmount is > 0m and <= 1_000_000_000m ? state.LastTargetAmount : null;
        return new AiConversationState(
            intent,
            Clamp(state.LastSearchText),
            Clamp(state.LastCycleHint),
            Clamp(state.LastWishlistReference),
            Clamp(state.LastResolvedCycle),
            matchedIds is { Count: > 0 } ? matchedIds : null,
            state.LastWishlistItemId is > 0 ? state.LastWishlistItemId : null,
            Clamp(state.LastCategory),
            cycleKeys is { Count: > 0 } ? cycleKeys : null,
            threshold,
            state.LastExcludeTransfers,
            excludedCategories,
            includedCategories,
            ledgerCategory,
            transactionType,
            exactDate,
            state.LastComparison,
            Clamp(state.LastRecurringReference),
            intents is { Count: > 0 } ? intents : null,
            topic,
            facets is { Count: > 0 } ? facets : null,
            recurringStatus,
            wishlistStatus,
            targetAmount);
    }

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

    // Coarse inflow/outflow/transfer classification of a request, stored on the frame so a
    // follow-up keeps the same money-direction filter. Best-effort keyword match; null when the
    // request doesn't lean one way.
    private static string? DetectTransactionType(string queryText)
    {
        // Mentioning transfers in an exclusion ("spending without transfers") describes the
        // boundary, not the requested transaction type. Only a positive transfer request is typed
        // as transfer.
        if (!ExcludeTransfersSignal.IsMatch(queryText) &&
            Regex.IsMatch(queryText, @"\b(?:show|list|find|only|just|my|all)?\s*transfers?\b|\btransfer transactions?\b", RegexOptions.IgnoreCase))
            return "transfer";
        if (Regex.IsMatch(queryText, @"\b(income|inflow|inflows|earnings?|salary|paychecks?|deposits?|received|credited?)\b", RegexOptions.IgnoreCase)) return "inflow";
        if (Regex.IsMatch(queryText, @"\b(outflow|outflows|expenses?|spending|spent|spend|withdrawals?|debited?)\b", RegexOptions.IgnoreCase)) return "outflow";
        return null;
    }

    private const string MonthNamePattern = "jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?";

    private static readonly Regex CycleComparisonSignal = new(
        @"\b(compare|comparison|vs\.?|versus|trend|history|historical|over time|each month|every month|past (few |\d+ )?months?|past (few |\d+ )?cycles?|year over year|month over month)\b",
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

    private static int CountLedgerDraftListRecords(string message)
    {
        var lines = message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        if (lines.Count is < 1 or > AiResponseSchemas.MaxChatActions) return 0;
        if (lines.Any(line =>
                line.Contains('?') ||
                Regex.IsMatch(line, @"^(?:how|what|which|when|where|why|who|can|could|would|should|did|do|does|is|are|show|find|list)\b",
                    RegexOptions.IgnoreCase)))
        {
            return 0;
        }

        var everyLineIsADraft = lines.All(line => Regex.IsMatch(
            line,
            @"^[\p{L}\p{N}][\p{L}\p{N}&'().,+/ -]{0,159}?\s+" +
            @"(?:(?:rm|myr|usd|sgd|eur|gbp|aud|cad|jpy|cny|rmb|\$|€|£)\s*)?" +
            // A single record is often typed as a sum of its parts ("Mamak 18+2.30" -- the meal
            // plus the drink). That is still one record, so it must still pin the response to one
            // action; without this the amount read as unparseable, the count fell to zero, the
            // schema stopped requiring an action, and the model answered with a staging sentence
            // and no draft behind it.
            @"\d{1,9}(?:[.,]\d{1,2})?(?:\s*\+\s*\d{1,9}(?:[.,]\d{1,2})?)*" +
            @"(?:\s+(?:income|inflow|outflow|expense|refund|deposit|withdrawal|" +
            @"transfer(?:\s+from\s+[\p{L}]+\s+to\s+[\p{L}]+)?|" +
            @"essentials?|growth|stability|rewards?))*\s*$",
            RegexOptions.IgnoreCase));
        return everyLineIsADraft ? lines.Count : 0;
    }

    // "how much / how many / total / average" questions are answered from the cycle summary
    // aggregates, so they never need the per-row detail block -- even though a phrase like
    // "how much did I spend" trips TransactionDetailSignal on the incidental "did i".
    private static readonly Regex AggregateQuestionSignal = new(
        @"\b(how much|how many|total|totals|average|averages|avg|breakdown|sum)\b",
        RegexOptions.Compiled);

    private static readonly Regex CountQuestionSignal = new(
        @"\b(how many|how often|how frequently|number of times|times did i|played|visited|frequency)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ...unless the user explicitly asks to see the individual records. These words mean the
    // detail sample is genuinely wanted and override the aggregate suppression above.
    private static readonly Regex ExplicitRecordSignal = new(
        @"\b(which|show|list|find|search|each|when did|edit|update|change|modify|delete|remove|erase|export|download|receipt|merchant|entry|entries)\b",
        RegexOptions.Compiled);

    private static readonly Regex RecurringSignal = new(
        @"\b(recurring|subscription|subscriptions|sub|subs|membership|memberships|renewal|renewals|renews?|bill|bills|instalments?|installments?|standing orders?|monthly payment|yearly payment|annual payment|autopay|auto-pay|auto-renewal|auto renewal|direct debit|direct debits|payment reminders?|bill reminders?|subscription reminders?|push reminders?)\b",
        RegexOptions.Compiled);

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
        IReadOnlyList<string>? Ambiguities = null);

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
        IReadOnlyList<string>? ambiguities = null)
    {
        var intents = (rawIntents ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i.Trim().ToLowerInvariant())
            .Where(KnownIntents.Contains)
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
            Clean(wishlistReference), Clean(transactionReference), constraints, cleanAmbiguities);
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
