using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

// Wire contract carried back to the client on AiChatResponse and echoed on the next
// AiChatRequest. Holds only structured references and validated user-supplied query parameters
// (never observed balances or full records) so short
// follow-ups ("those", "the previous cycle", "it") can be resolved. Treated as untrusted on
// the way back in (see AiAssistantService.SanitizeConversationState) -- IDs are re-derived
// against the DB, never trusted verbatim.
// Typed amount comparison carried on the conversation frame (comparator + low + optional high),
// stored typed rather than as text so no information is lost across turns; canonical display text
// is generated only when a follow-up is expanded (AiAssistantService.FormatAmountThreshold).
// Comparator is one of AmountComparator's names ("GreaterThan", "Between", ...).
public sealed record AiAmountThreshold(string Comparator, decimal Low, decimal? High = null);

public sealed record AiConversationState(
    string? LastIntent,
    string? LastSearchText,
    string? LastCycleHint,
    string? LastWishlistReference,
    string? LastResolvedCycle = null,
    IReadOnlyList<string>? LastMatchedTransactionIds = null,
    int? LastWishlistItemId = null,
    string? LastCategory = null,
    // Resolved query "frame" carried across turns so a short follow-up ("how about last cycle",
    // "what about over 200") overrides only the dimension it names and inherits the rest -- rather
    // than the old approach of re-parsing the prior message's raw text, which let stale wording
    // ("this cycle") collide with the new turn. Cycles are concrete "yyyy-MM" keys (never
    // "this"/"last"); the threshold is typed; the remaining filters persist so exclusions/
    // inclusions, transaction type, exact date, comparison scope, category and references survive a
    // follow-up. Untrusted like every other client-echoed field (see SanitizeConversationState) --
    // re-validated / re-parsed against the DB and canonical parsers, never trusted verbatim.
    IReadOnlyList<string>? LastResolvedCycleKeys = null,
    AiAmountThreshold? LastAmountThreshold = null,
    bool LastExcludeTransfers = false,
    IReadOnlyList<string>? LastExcludedCategories = null,
    IReadOnlyList<string>? LastIncludedCategories = null,
    string? LastLedgerCategory = null,
    string? LastTransactionType = null,
    string? LastExactDate = null,
    bool LastComparison = false,
    string? LastRecurringReference = null,
    // The full resolved intent set of the prior turn. LastIntent keeps just the primary intent for
    // back-compat; this list lets a modifier-only follow-up ("how about previous cycle") re-run the
    // same analysis (e.g. anomaly/duplicate detection) rather than collapsing to a plain total.
    IReadOnlyList<string>? LastIntents = null,
    // Topic disambiguates mixed-intent requests such as "how much do subscriptions cost?", whose
    // primary intent may be a ledger total even though the conversation is about subscriptions.
    string? LastTopic = null,
    // Closed-vocabulary operations such as anomaly/duplicates/daily-extreme/recurring-cost. These
    // preserve what calculation to repeat when a follow-up supplies only a new scope.
    IReadOnlyList<string>? LastQueryFacets = null,
    string? LastRecurringStatus = null,
    string? LastWishlistStatus = null,
    // A user-supplied forecast target is a query parameter (like LastAmountThreshold), never an
    // observed balance or record amount. It is range-checked again when echoed by the client.
    decimal? LastTargetAmount = null);

public sealed record AiChatMessage(string Role, string Content);
public sealed record AiChatRequest(string Message, IReadOnlyList<AiChatMessage>? History, AiConversationState? State = null);
public sealed record AiChatResponse(string Reply, IReadOnlyList<AiUiAction> Actions, bool CloseChat = false, AiConversationState? State = null);
public sealed record AiUiAction(string Type, Dictionary<string, object?> Payload);

// AiChatResponse alone is the wire shape returned to the client either way (a friendly
// message is a valid chat reply whether or not the AI provider itself succeeded) -- but the
// controller still needs to know whether to report 200 or 503, the same way every other AI
// endpoint's controller switches on a Status field instead of guessing from the payload.
public sealed record AiChatOutcome(AiChatResponse Response, bool IsProviderError);

public partial class AiAssistantService
{
    private static readonly string[] LedgerCategories = ["Essentials", "Growth", "Stability", "Rewards", "Income"];
    private static readonly HashSet<string> AllowedActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openLedger",
        "openDashboard",
        "openRecurring",
        "openWishlist",
        "openSettings",
        "openAddLedgerDraft",
        "openAddRecurringDraft",
        "openAddWishlistDraft",
        "openEditLedgerDraft",
        "openEditRecurringDraft",
        "openEditWishlistDraft"
    };

    private const int MaxMessageLength = 2000;
    private const int MaxHistoryMessageLength = 2000;
    // Defensive ceiling on how many rows a single cycle range can pull into memory. Cycle
    // aggregates (income/outflow/category spend) only ever surface bounded summaries to the
    // model, but without a cap a heavy user's multi-cycle comparison would stream every row
    // in each range out of Postgres. If a range is truncated, DataScope flags it so the model
    // knows the aggregates may be partial rather than silently reporting them as complete.
    private const int MaxTransactionsPerRange = 2000;

    private readonly AiClient _aiClient;
    private readonly AppDbContext _context;
    private readonly TransactionCategoryService _categoryService;

    public AiAssistantService(
        AiClient aiClient,
        AppDbContext context,
        TransactionCategoryService categoryService)
    {
        _aiClient = aiClient;
        _context = context;
        _categoryService = categoryService;
    }

    public async Task<AiChatOutcome> ChatAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        var message = (request.Message ?? string.Empty).Trim();
        var priorState = SanitizeConversationState(request.State);
        // An explicit reset ("never mind", "start over", "forget that", "new question") abandons
        // the frame before any canned/guardrail response is produced.
        if (!string.IsNullOrWhiteSpace(message) && IsContextResetRequest(message)) priorState = null;
        if (string.IsNullOrWhiteSpace(message))
        {
            return Ok(new AiChatResponse("Please ask a financial question or tell me what you want to open.", [], State: priorState));
        }
        if (message.Length > MaxMessageLength)
        {
            return Ok(new AiChatResponse("That message is too long. Please shorten it and try again.", [], State: priorState));
        }
        if (LooksLikeDeleteCommand(message))
        {
            return Ok(new AiChatResponse("I'm unable to delete records. You can delete it manually from the app if sensitive mode is off.", [], State: priorState));
        }
        if (TryHandleSmallTalk(message, out var smallTalkResponse))
        {
            return Ok(smallTalkResponse! with { State = smallTalkResponse!.CloseChat ? null : priorState });
        }

        if (!_aiClient.IsConfigured)
        {
            return Ok(new AiChatResponse("AI chat is not configured on the server.", [], State: priorState));
        }

        var history = SanitizeHistory(request.History);
        var intentPlan = ResolveDeterministically(message, priorState);
        if (intentPlan.Confidence < 0.72 || intentPlan.Intents.Contains(AiIntent.General))
        {
            var classified = await TryClassifyIntentAsync(message, priorState, cancellationToken);
            if (classified != null && classified.Confidence >= intentPlan.Confidence)
            {
                intentPlan = MergeResolutions(message, classified, priorState);
            }
        }

        var contextResult = await BuildContextAsync(intentPlan, cancellationToken);
        var context = contextResult.Context;
        // Generic sufficiency gate: any intent that needs an exact figure whose dataset is
        // missing (hidden/unavailable) is answered deterministically here rather than handed to
        // the model with a hole in its context.
        var insufficient = ResolveInsufficiency(
            intentPlan.Intents.Select(ToIntentName).ToList(),
            contextResult.Sufficiency);
        if (insufficient != null)
        {
            // A privacy/empty-data clarification is still part of the same conversation. Returning
            // no state here made the client erase the frame precisely when a follow-up was likely.
            return Ok(insufficient with { State = contextResult.OutgoingState });
        }
        // A hypothetical ("what if I changed this to 25") is a question, not an edit command --
        // never resolve it into an openEditLedgerDraft action.
        if (!intentPlan.Constraints.Hypothetical)
        {
            var resolvedEdit = await TryResolveLedgerEditAsync(
                message,
                context,
                contextResult.TargetCycles,
                contextResult.CycleDay,
                contextResult.DefaultYear,
                intentPlan.QueryPlan.TransactionIds,
                cancellationToken);
            if (resolvedEdit != null)
            {
                // Carry the structured references (matched ids, resolved cycle, search text) even
                // on a "which one?" clarification, so a follow-up like "the one named Badminton"
                // or "that one" can be narrowed against the same candidate set next turn.
                return Ok(resolvedEdit with { State = resolvedEdit.State ?? contextResult.OutgoingState });
            }
        }

        // Data-query follow-ups are fully reconstructed server-side (BuildQueryText folds the
        // resolved frame into the request), so the model needs NO prior dialogue for them -- that
        // prose is never sent, saving tokens. The exception is a *semantic* follow-up ("why?", "is
        // that good?", "explain that") that refers to the assistant's own previous conclusion
        // rather than to data: for those we send a bounded last exchange so "that"/"it" resolves.
        var promptHistory = IsSemanticFollowUp(message) ? BoundedSemanticHistory(history) : [];
        var systemInstruction = BuildSystemInstruction();
        var userContent = BuildUserContent(message, promptHistory, context);

        string text;
        try
        {
            text = await _aiClient.GenerateTextAsync(
                [AiPart.FromText(userContent)],
                new AiGenerationOptions(
                    Feature: "chat",
                    Temperature: 0.15,
                    MaxOutputTokens: intentPlan.QueryPlan.NeedsCycleComparison ? 1100 : 800,
                    SystemInstruction: systemInstruction,
                    ResponseJsonSchema: AiResponseSchemas.Chat,
                    ThinkingLevel: intentPlan.QueryPlan.NeedsCycleComparison ? "medium" : "low",
                    ModelConfigurationKey: "AiModels:Chat"),
                cancellationToken);
        }
        catch (AiClientException ex)
        {
            // Matches every other AI endpoint: a Status-style signal the controller switches
            // on to pick 200 vs 503, not a thrown exception crossing the service boundary --
            // the friendly reply text is still the same AiChatResponse shape either way.
            return new AiChatOutcome(new AiChatResponse(ex.Message, []), IsProviderError: true);
        }

        var parsed = ParseAndValidateResponse(text, context, message, intentPlan.Constraints);
        // Phase 5: an incomplete aggregate must never surface as a bare exact figure.
        parsed = parsed with { Reply = EnforceApproximateWording(parsed.Reply, contextResult.Sufficiency.Approximate) };
        // Round-trip the structured references so the client can echo them back on the next
        // turn (see AiConversationState). Not attached to small-talk/guardrail replies -- those
        // deliberately carry no financial state.
        return Ok(parsed with { State = contextResult.OutgoingState });
    }

    private static AiChatOutcome Ok(AiChatResponse response) => new(response, IsProviderError: false);

    private static readonly HashSet<string> FarewellPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "bye", "goodbye", "bye bye", "see you", "see ya", "later", "cya"
    };

    private static readonly HashSet<string> GreetingPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "hi", "hello", "hey", "hiya", "yo", "sup", "good morning", "good afternoon", "good evening"
    };

    private static readonly HashSet<string> AcknowledgmentPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "thanks", "thank you", "ty", "thx", "cheers",
        "ok", "okay", "k", "kk", "cool", "great", "nice", "awesome", "perfect",
        "got it", "sounds good", "alright", "sure", "yep", "yeah"
    };

    // Greetings/thanks/acks carry zero financial intent -- answering them never needed the
    // model at all, so this skips the AI call (and its context-building work) entirely rather
    // than just trimming what gets sent. Deliberately an exact-match closed list (after
    // stripping trailing punctuation), not a `Contains` check, so it never fires on a real
    // question that merely starts or ends with "thanks" or "ok".
    private static bool TryHandleSmallTalk(string message, out AiChatResponse? response)
    {
        var normalized = Regex.Replace(message.Trim(), @"[!.?,]+$", "").Trim().ToLowerInvariant();

        if (FarewellPhrases.Contains(normalized))
        {
            response = new AiChatResponse("Bye! I'm here whenever you need me.", [], CloseChat: true);
            return true;
        }
        if (GreetingPhrases.Contains(normalized))
        {
            response = new AiChatResponse("Hi! Ask me a financial question or tell me what you'd like to open.", []);
            return true;
        }
        if (AcknowledgmentPhrases.Contains(normalized))
        {
            response = new AiChatResponse("Anytime! Let me know if you need anything else.", []);
            return true;
        }

        response = null;
        return false;
    }

    private static readonly Regex FollowUpSignal = new(
        @"^(and|also|what about|how about|what if|then|now|but|actually|instead|alternatively|next)\b|\b(those|these|it|them|the other|other one|same|both|either|former|latter|above|earlier|previous result|one before|one after)\b|\b(?:that|this)\b(?!\s+(?:cycle|month|year|quarter|day|week))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Biased toward keeping history: only a longer message with no continuation marker is
    // treated as a fresh, self-contained question. Short replies ("just food", "March") and
    // anything referencing "it"/"that"/"the other one" almost always depend on the prior
    // turn, so those still get history -- this only trims it for the messages least likely
    // to need it.
    private static bool NeedsHistoryContext(string message)
    {
        var trimmed = message.Trim();
        var wordCount = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return FollowUpSignal.IsMatch(trimmed) || ContinuationModifierSignal.IsMatch(trimmed)
            || IsRelativeCyclePhrase(trimmed) || ResolveRelativeDate(trimmed, "2000-01-15") != null
            || (wordCount <= 5 && !IsSelfContainedFinancialRequest(trimmed));
    }

    // A *semantic* follow-up asks about the assistant's own previous answer/conclusion ("why?", "is
    // that good?", "explain that", "should I be worried?") rather than requesting data that can be
    // reconstructed from the frame. These need the model to see the prior exchange, so a bounded
    // last user+assistant pair is sent for them (and only them).
    private static readonly Regex SemanticFollowUpSignal = new(
        @"^(?:why\b|why\?|how come\b|how so\b|really\??$|and\?$|so\?$|meaning\??$|compared to what\b|based on what\b)" +
        @"|\bis that (?:good|bad|normal|a lot|too (?:much|high|low)|ok|okay|fine|healthy|concerning|worrying|expensive|cheap)\b" +
        @"|\b(?:is|was|does|did|can|could|would) (?:that|this|it)\b.{0,45}\b(?:mean|include|exclude|matter|count|seem|make sense|affect|change|good|bad|normal|right|correct)\b" +
        @"|\bgood or bad\b|\bshould i (?:be )?(?:worry|worried|concerned)\b" +
        @"|\b(?:explain|elaborate|clarify)(?: that| this| it)?\b|\bwhat (?:do|does) (?:you|that|this|it) mean\b" +
        @"|\btell me more\b|\bexpand on (?:that|this|it)\b|\bbreak (?:that|this|it) down\b" +
        @"|\bwhat (?:caused|drove|contributed to|explains) (?:that|this|it)\b|\bwhy (?:is|was|did|does) (?:that|this|it)\b" +
        @"|\bhow did you (?:calculate|work out|derive|get) (?:that|this|it)\b|\bare you sure\b|\bwhat (?:should|can|could) i do about (?:that|this|it)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsSemanticFollowUp(string message) => SemanticFollowUpSignal.IsMatch(message.Trim());

    // Explicit "drop the context" phrasing. Kept tight (leading phrase or standalone) so it never
    // fires on an ordinary question that merely contains one of these words.
    private static readonly Regex ContextResetSignal = new(
        @"^(?:never ?mind|forget (?:that|it|about that|everything)|start over|start again|reset|clear (?:that|it|context|everything)|new (?:question|topic)|different (?:question|topic)|unrelated|change of topic|scratch that|ignore (?:that|the above|previous))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsContextResetRequest(string message) => ContextResetSignal.IsMatch(message.Trim());

    // The minimal prior context a semantic follow-up needs: the last assistant turn (its
    // conclusion) plus the user turn that prompted it. Already sanitized/length-capped upstream.
    private static IReadOnlyList<AiChatMessage> BoundedSemanticHistory(IReadOnlyList<AiChatMessage> history) =>
        history.Count <= 2 ? history : history.TakeLast(2).ToList();

    // Compact canonical one-liner of the prior resolved frame for the intent classifier -- carries
    // enough to disambiguate a short follow-up without sending any prior message prose.
    private static string SummarizePriorFrame(AiConversationState? state)
    {
        if (state == null) return string.Empty;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.LastIntent)) parts.Add($"intent={state.LastIntent}");
        if (state.LastResolvedCycleKeys is { Count: > 0 } cycles) parts.Add($"cycles={string.Join(",", cycles)}");
        if (FormatAmountThreshold(state.LastAmountThreshold) is { } threshold) parts.Add($"threshold={threshold}");
        if (!string.IsNullOrWhiteSpace(state.LastSearchText)) parts.Add($"search={state.LastSearchText}");
        if (state.LastComparison) parts.Add("comparison=true");
        if (!string.IsNullOrWhiteSpace(state.LastTopic)) parts.Add($"topic={state.LastTopic}");
        if (state.LastQueryFacets is { Count: > 0 } facets) parts.Add($"operations={string.Join(",", facets)}");
        if (!string.IsNullOrWhiteSpace(state.LastRecurringStatus)) parts.Add($"recurringStatus={state.LastRecurringStatus}");
        return string.Join("; ", parts);
    }

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
                    ResponseJsonSchema: AiResponseSchemas.IntentClassification,
                    ThinkingLevel: "none",
                    ModelConfigurationKey: "AiModels:IntentClassifier"),
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
        "did", "do", "does", "have", "has", "i", "we", "it", "that", "this", "them", "those", "these", "the", "a", "an"
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

    private static string NormalizeSearchText(string value) =>
        Regex.Replace(value.Trim(), @"^(?:my|the)\s+", string.Empty, RegexOptions.IgnoreCase);

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
            .TakeLast(6)
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
        "recurring.add", "recurring.edit", "allocation.balance", "allocation.performance",
        "navigation", "general"
    };

    private const int MaxStateSearchLength = 80;
    private const int MaxStateMatchedIds = 50;

    // Client-carried conversation state is untrusted input, exactly like history. An unknown
    // intent, an over-long search string, or a giant id list must never flow into query
    // building unchecked. IDs here are only *hints*; they are re-derived/re-validated against
    // the DB before being surfaced again, never trusted verbatim.
    private static AiConversationState? SanitizeConversationState(AiConversationState? state)
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
        @"\b(spend|spent|spending|budget|income|earnings?|salary|paychecks?|cash ?flow|outflow|inflow|balance|total|average|net|save|saved|savings|essentials|growth|stability|rewards|cycle|this month|last month|how much|money going|doing better|doing worse|afford|financial health|performance)\b",
        RegexOptions.Compiled);

    private static readonly Regex TransactionDetailSignal = new(
        @"\b(transaction|transactions|ledger|purchase|purchased|bought|paid|payment|receipt|charge|charged|expense|expenses|deposit|deposits|withdrawal|withdrawals|refund|refunds|debit|debits|credit|credits|find|search|when did|did i|edit|update|change|modify|record|entry|merchant|cost me|how often|how frequently|frequency|largest|biggest|highest|lowest|smallest|most expensive|cheapest)\b",
        RegexOptions.Compiled);

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
        @"\b(which|show|list|find|search|each|when did|edit|update|change|modify|receipt|merchant|entry|entries)\b",
        RegexOptions.Compiled);

    private static readonly Regex RecurringSignal = new(
        @"\b(recurring|subscription|subscriptions|membership|memberships|renewal|renewals|renews?|bill|bills|instalments?|installments?|standing orders?|monthly payment|autopay|auto-pay)\b",
        RegexOptions.Compiled);

    private static readonly Regex WishlistSignal = new(
        @"\b(wishlist|wish list|bucket list|dream purchase|next purchase|want to buy|planning to buy|saving for|priority item|afford|goal|goals|savings? goal|savings? target)\b",
        RegexOptions.Compiled);

    private static readonly Regex WishlistForecastSignal = new(
        @"\b(how long|when can i|when will i|reach|hit|achieve|afford|target date|months? until|cycles? until)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Coaching/advice intent -- "how do I improve", "where can I cut", "am I on track".
    // Grounding this kind of answer needs the user's allocation targets, not just actuals,
    // so it turns on the budgetTargets block (and cycle summaries) the same way analysis does.
    private static readonly Regex ImprovementSignal = new(
        @"\b(improve|improving|reduce|reducing|cut|cutting|spend less|save more|advice|advise|suggest|suggestion|recommend|recommendation|on track|over ?budget|under ?budget|overspend|overspending|should i|where can i|too much|tips?|optimi[sz]e)\b",
        RegexOptions.Compiled);

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

    private enum TransactionDataLevel
    {
        None,
        AggregateOnly,
        MatchingRows,
        BoundedSample
    }

    private enum DerivedMetric
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

    // The authoritative typed query plan. Carries the typed intents plus every data-loading
    // decision derived from them (what transaction level to pull, which derived metrics to
    // compute, and which optional blocks to include). The plan is the sole data-loading contract.
    private sealed record AiQueryPlan(
        IReadOnlyList<AiIntent> Intents,
        TransactionDataLevel TransactionData,
        IReadOnlyList<DerivedMetric> Metrics,
        string? SearchText,
        string? CycleHint,
        string QueryText,
        IReadOnlyList<string> TransactionIds,
        int? WishlistItemId,
        bool NeedsTransactionDetail,
        bool NeedsCycleSummary,
        bool NeedsCycleComparison,
        bool NeedsWishlist,
        bool NeedsWishlistForecast,
        bool NeedsRecurring,
        bool NeedsBudgetTargets);

    private sealed record TargetCycleSelection(IReadOnlyList<CycleKey> Cycles, bool ExplicitlyRequested);
    internal sealed record AiTransactionRow(
        string Id,
        DateTime Timestamp,
        string Date,
        string Description,
        string Category,
        string LedgerCategory,
        decimal Amount);
    private sealed record AiTransactionDbRow(
        string Id,
        DateTime Date,
        string Description,
        string Category,
        string LedgerCategory,
        decimal Amount);
    internal sealed record AiWishlistRow(int Id, string Name, decimal Price, string Priority, bool IsActive, bool IsPurchased, DateTime CreatedAt, DateTime? PurchasedAt = null);
    private sealed record TransactionDateRange(DateTime Start, DateTime End);

    private sealed record ContextSufficiency(bool Complete, bool Approximate, IReadOnlyList<AiDatasetKey> Missing);
    private sealed record AiContextBuildResult(
        AiContext Context,
        IReadOnlyList<CycleKey> TargetCycles,
        int CycleDay,
        int DefaultYear,
        ContextSufficiency Sufficiency,
        AiConversationState OutgoingState);

    private async Task<AiContextBuildResult> BuildContextAsync(
        AiIntentPlan intentPlan,
        CancellationToken cancellationToken)
    {
        var queryPlan = intentPlan.QueryPlan;
        var setting = await LoadFinancialSettingAsync(cancellationToken);
        var cycleDay = setting?.CycleDay ?? 28;
        var selectedMonth = setting?.SelectedMonth ?? DateTime.Now.ToString("MMM");
        var selectedYear = setting?.SelectedYear ?? DateTime.Now.Year;
        var selectedMonthIndex = Array.IndexOf(FinancialConstants.MonthAbbreviations, selectedMonth) + 1;
        if (selectedMonthIndex <= 0) selectedMonthIndex = DateTime.Now.Month;

        var categories = (await _categoryService.GetCategoriesAsync())
            .Select(c => c.Name)
            .OrderBy(name => name)
            .ToList();

        var sensitiveMode = setting?.HideSensitive ?? true;
        // "how long until my Growth reaches 50000" -- a target-balance forecast for any ledger.
        var ledgerForecastRequest = sensitiveMode ? null : TryParseLedgerBalanceForecast(queryPlan.QueryText);
        DateOnly? exactDate = intentPlan.Entities?.Date;
        if (!exactDate.HasValue && TryExtractDate(queryPlan.QueryText, selectedYear, out var parsedExactDate, out _))
            exactDate = parsedExactDate;
        var targetSelection = ResolveTargetCycles(
            queryPlan.QueryText,
            selectedYear,
            selectedMonthIndex,
            queryPlan.NeedsCycleSummary,
            queryPlan.NeedsCycleComparison);
        if (exactDate.HasValue)
        {
            // A date can fall in the prior labelled cycle when cycleDay is not 1. Load the cycle
            // that actually contains it, then narrow rows to the exact calendar day below.
            targetSelection = new TargetCycleSelection([ResolveCycleContainingDate(exactDate.Value, cycleDay)], true);
        }
        if ((queryPlan.NeedsWishlistForecast || ledgerForecastRequest != null) && !targetSelection.ExplicitlyRequested)
        {
            // The active cycle plus the two before it -- matches the app's past-3 Rewards average
            // window (FinancialService.CalculatePastRewardsAverageFromTxs starts at the active
            // cycle), so the assistant's forecast lines up with the Wishlist page.
            targetSelection = new TargetCycleSelection(
                Enumerable.Range(0, 3)
                    .Select(offset => AddMonths(selectedYear, selectedMonthIndex, -offset))
                    .Select(value => new CycleKey(value.Year, value.MonthIndex))
                    .ToList(),
                false);
        }

        object recurringContext = Array.Empty<object>();
        var recurringRows = new List<AiRecurringRow>();
        if (queryPlan.NeedsRecurring)
        {
            recurringRows = await LoadRecurringRowsAsync(cancellationToken);
            recurringRows = DetectRecurringStatus(queryPlan.QueryText) switch
            {
                "active" => recurringRows.Where(r => r.Active).ToList(),
                "inactive" => recurringRows.Where(r => !r.Active).ToList(),
                _ => recurringRows
            };
            recurringContext = sensitiveMode
                ? recurringRows.Select(r => new
                {
                    r.Id, r.Name, r.Category, r.LedgerCategory,
                    r.StartDate, r.EndDate, r.DueDate, r.Active, r.Frequency, r.NextDueDate
                }).ToList()
                : recurringRows;
        }

        object wishlistContext = Array.Empty<object>();
        var wishlistRows = new List<AiWishlistRow>();
        if (queryPlan.NeedsWishlist)
        {
            var wishlist = await LoadWishlistRowsAsync(queryPlan.WishlistItemId, cancellationToken);
            wishlist = DetectWishlistStatus(queryPlan.QueryText) switch
            {
                "active" => wishlist.Where(w => w.IsActive && !w.IsPurchased).ToList(),
                "inactive" => wishlist.Where(w => !w.IsActive && !w.IsPurchased).ToList(),
                "purchased" => wishlist.Where(w => w.IsPurchased).ToList(),
                "unpurchased" => wishlist.Where(w => !w.IsPurchased).ToList(),
                _ => wishlist
            };
            wishlistRows = wishlist;
            wishlistContext = sensitiveMode
                ? wishlist.Select(w => new
                {
                    w.Id, w.Name, w.Priority, w.IsActive, w.IsPurchased, w.CreatedAt, w.PurchasedAt
                }).ToList()
                : wishlist;
        }

        var allTransactions = new List<AiTransactionRow>();
        var scopeTruncated = false;
        int? exactMatchCount = null;
        if (queryPlan.NeedsTransactionDetail || queryPlan.NeedsCycleSummary)
        {
            if (targetSelection.Cycles.Count > 0)
            {
                var seenIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var range in MergeCycleRanges(targetSelection.Cycles, cycleDay))
                {
                    if (queryPlan.TransactionData == TransactionDataLevel.MatchingRows && !string.IsNullOrWhiteSpace(queryPlan.SearchText))
                    {
                        exactMatchCount = (exactMatchCount ?? 0) + await CountTransactionsAsync(range.Start, range.End, queryPlan.SearchText, queryPlan.TransactionIds, cancellationToken);
                    }
                    var rows = await QueryTransactionsAsync(
                        range.Start,
                        range.End,
                        queryPlan.TransactionData == TransactionDataLevel.MatchingRows ? queryPlan.SearchText : null,
                        queryPlan.TransactionIds,
                        cancellationToken);
                    // QueryTransactionsAsync fetches one row beyond the cap: if it comes back we
                    // KNOW the range genuinely exceeded the cap (so aggregates are partial). This
                    // distinguishes exactly MaxTransactionsPerRange rows (complete) from more than
                    // that (truncated) -- the extra row is dropped and never surfaced.
                    if (rows.Count > MaxTransactionsPerRange)
                    {
                        scopeTruncated = true;
                        rows = rows.Take(MaxTransactionsPerRange).ToList();
                    }
                    foreach (var row in rows)
                    {
                        if (seenIds.Add(row.Id)) allTransactions.Add(row);
                    }
                }
            }
            else
            {
                // A detail request with no cycle clue (for example, "find Grab") keeps a
                // bounded recent fallback. Explicit/relative cycle requests never use it.
                var rows = await LoadRecentFallbackTransactionsAsync(
                    queryPlan.TransactionData == TransactionDataLevel.MatchingRows ? queryPlan.SearchText : null,
                    queryPlan.TransactionIds,
                    cancellationToken);
                allTransactions.AddRange(rows);
            }
        }

        allTransactions = allTransactions
            .OrderByDescending(t => t.Timestamp)
            .ThenByDescending(t => t.Id)
            .ToList();

        if (exactDate.HasValue)
        {
            allTransactions = allTransactions
                .Where(t => DateOnly.FromDateTime(t.Timestamp) == exactDate.Value)
                .ToList();
            if (exactMatchCount.HasValue) exactMatchCount = allTransactions.Count;
        }

        // Phase 2: enforce scope exclusions deterministically before any aggregate, detail
        // sample, or derived metric is built -- "excluding rent" / "without transfers" must
        // remove those rows from every downstream number, not just be hinted to the model.
        var constraints = intentPlan.Constraints;
        var (excludedCategories, excludedLedgerCategories) = ResolveConstraintCategories(
            constraints.ExcludedCategories, categories, LedgerCategories);
        var (includedCategories, includedLedgerCategories) = ResolveConstraintCategories(
            constraints.IncludedCategories, categories, LedgerCategories);
        if (constraints.ExcludeTransfers || excludedCategories.Count > 0 || excludedLedgerCategories.Count > 0)
        {
            allTransactions = allTransactions.Where(t =>
                    !(constraints.ExcludeTransfers && IsTransfer(t)) &&
                    !excludedCategories.Contains(t.Category, StringComparer.OrdinalIgnoreCase) &&
                    !excludedLedgerCategories.Contains(t.LedgerCategory, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        if (includedCategories.Count > 0 || includedLedgerCategories.Count > 0)
        {
            allTransactions = allTransactions.Where(t =>
                    includedCategories.Contains(t.Category, StringComparer.OrdinalIgnoreCase) ||
                    includedLedgerCategories.Contains(t.LedgerCategory, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        var requestedTransactionType = DetectTransactionType(queryPlan.QueryText);
        var scopeFacets = DetectQueryFacets(queryPlan.QueryText);
        var appliesTransactionTypeFilter = requestedTransactionType != null &&
            !scopeFacets.Contains("daily_extreme", StringComparer.Ordinal) &&
            (scopeFacets.Contains("list", StringComparer.Ordinal) || scopeFacets.Contains("activity_count", StringComparer.Ordinal));
        if (appliesTransactionTypeFilter)
        {
            allTransactions = allTransactions.Where(t => requestedTransactionType switch
            {
                "inflow" => t.Amount > 0 && !IsTransfer(t),
                "outflow" => t.Amount < 0 && !IsTransfer(t),
                "transfer" => IsTransfer(t),
                _ => true
            }).ToList();
            if (exactMatchCount.HasValue) exactMatchCount = allTransactions.Count;
        }

        // Whether an exact cycle total is even eligible to be recovered: SumOutflowAsync cannot
        // express category exclusions, so an "exact" figure that ignored them would lie.
        var cycleTotalRecoverable = targetSelection.Cycles.Count > 0
            && excludedCategories.Count == 0
            && excludedLedgerCategories.Count == 0
            && includedCategories.Count == 0
            && includedLedgerCategories.Count == 0
            && (!appliesTransactionTypeFilter || requestedTransactionType == "outflow");

        object recentTransactions;
        if (!queryPlan.NeedsTransactionDetail)
        {
            recentTransactions = Array.Empty<object>();
        }
        else if (sensitiveMode)
        {
            recentTransactions = allTransactions.Take(120).Select(t => new
            {
                t.Id,
                t.Date,
                t.Description,
                t.Category,
                t.LedgerCategory,
                txType = t.Amount < 0 ? "outflow" : t.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase) ? "transfer" : "inflow"
            }).ToList();
        }
        else
        {
            recentTransactions = allTransactions.Take(120).Select(t => new
            {
                t.Id,
                t.Date,
                t.Description,
                t.Category,
                t.LedgerCategory,
                t.Amount,
                txType = t.Amount < 0 ? "outflow" : t.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase) ? "transfer" : "inflow"
            }).ToList();
        }

        var activeCycleStart = CategoryAttributionService.GetCycleRange(selectedYear, selectedMonthIndex, cycleDay).start;
        var wishlistReference = intentPlan.QueryPlan.SearchText
            ?? intentPlan.ConversationState.LastWishlistReference;
        // Ledger balances derived from the opening balance at the active cycle start plus this
        // cycle's per-ledger net -- mirrors the dashboard/Wishlist page. Computed once and reused
        // for: the wishlist forecast's "remaining" (Rewards balance), the affordable-item count,
        // and stability-fund progress. Only when a question actually needs it, and never in
        // sensitive mode (all three are amount-based, which sensitive mode refuses anyway).
        var wantsAffordableCount = queryPlan.NeedsWishlist &&
            Regex.IsMatch(queryPlan.QueryText, @"\b(how many|afford|can i (?:buy|afford|get))\b", RegexOptions.IgnoreCase);
        var wantsStabilityProgress = Regex.IsMatch(queryPlan.QueryText,
            @"\bstability\b.{0,40}\b(fund|goal|target|on track|progress|reach(?:ed)?|close|there yet|percent)\b|\b(goal|target|on track|progress|reach(?:ed)?|percent)\b.{0,40}\bstability\b",
            RegexOptions.IgnoreCase);
        decimal rewardsBalance = 0m;
        object? stabilityProgress = null;
        int? affordableWishlistCount = null;
        object? ledgerBalanceForecast = null;
        // Balance/forecast math must run over UNFILTERED cycle transactions. The primary load may
        // be narrowed to a merchant/activity search (e.g. "how many wishlist items can I afford"
        // leaks a bogus searchText), which would zero out every ledger balance. When the load was
        // filtered, re-fetch the needed cycles unfiltered; otherwise reuse allTransactions as-is.
        var needsLedgerTxs = !sensitiveMode && (queryPlan.NeedsWishlistForecast || wantsAffordableCount || wantsStabilityProgress || ledgerForecastRequest != null);
        var transactionsWereFiltered = queryPlan.TransactionData == TransactionDataLevel.MatchingRows &&
            !string.IsNullOrWhiteSpace(queryPlan.SearchText) || appliesTransactionTypeFilter ||
            excludedCategories.Count > 0 || excludedLedgerCategories.Count > 0 ||
            includedCategories.Count > 0 || includedLedgerCategories.Count > 0;
        var ledgerTransactions = allTransactions;
        if (needsLedgerTxs && transactionsWereFiltered && targetSelection.Cycles.Count > 0)
        {
            ledgerTransactions = await LoadUnfilteredCycleTransactionsAsync(targetSelection.Cycles, cycleDay, cancellationToken);
        }
        if (needsLedgerTxs)
        {
            var opening = await new CycleBalanceService(_context).GetOpeningBalanceAsync(selectedYear, selectedMonthIndex, cycleDay);
            var activeCycleTxs = ledgerTransactions
                .Where(t => IsInCycle(t, new CycleKey(selectedYear, selectedMonthIndex), cycleDay))
                .ToList();
            decimal LedgerNet(string ledgerCategory) => activeCycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(new Models.Transaction
            {
                Amount = t.Amount,
                LedgerCategory = t.LedgerCategory
            }, ledgerCategory));
            rewardsBalance = opening.rewards + LedgerNet("Rewards");
            if (wantsStabilityProgress)
            {
                var stabilityBalance = opening.stability + LedgerNet("Stability");
                var target = setting?.TargetStabilityFund ?? 0m;
                stabilityProgress = new
                {
                    currentStabilityBalance = stabilityBalance,
                    targetStabilityFund = target,
                    percentReached = target > 0 ? Math.Round(stabilityBalance / target * 100m, 1) : (decimal?)null,
                    remaining = target > 0 ? Math.Max(0m, target - stabilityBalance) : (decimal?)null
                };
            }
            if (wantsAffordableCount)
            {
                affordableWishlistCount = wishlistRows.Count(w => !w.IsPurchased && rewardsBalance >= w.Price);
            }
            if (ledgerForecastRequest is { } forecast)
            {
                var openingFor = forecast.Ledger switch
                {
                    "Essentials" => opening.essentials,
                    "Growth" => opening.growth,
                    "Stability" => opening.stability,
                    "Rewards" => opening.rewards,
                    _ => 0m
                };
                var currentBalance = openingFor + LedgerNet(forecast.Ledger);
                ledgerBalanceForecast = BuildLedgerBalanceForecast(ComputeLedgerBalanceForecast(
                    forecast.Ledger, forecast.Target, currentBalance,
                    ledgerTransactions, targetSelection.Cycles, cycleDay, DateTime.Now));
            }
        }
        var wishlistForecast = queryPlan.NeedsWishlistForecast && !sensitiveMode
            ? BuildWishlistForecast(new WishlistForecastPolicy(
                    wishlistRows,
                    ledgerTransactions,
                    targetSelection.Cycles,
                    cycleDay,
                    activeCycleStart,
                    DateTime.Now,
                    wishlistReference,
                    rewardsBalance))
            : null;

        // The user's allocation goals: fractions of income per ledger category plus the
        // stability-fund target. Actuals live in cycleSummaries.ledgerNet -- these targets are
        // what makes "how am I doing" / "where can I cut" answerable rather than guessed. Only
        // sent for analysis/coaching questions, and never in sensitiveMode (advice is
        // inherently amount-based, which sensitiveMode refuses anyway).
        object? budgetTargets = queryPlan.NeedsBudgetTargets && !sensitiveMode
            ? new
            {
                note = "Fractions of income allocated per ledger category. Compare against cycleSummaries.ledgerNet.",
                essentials = setting?.EssentialsAlloc ?? 0.50m,
                growth = setting?.GrowthAlloc ?? 0.25m,
                stability = setting?.StabilityAlloc ?? 0.15m,
                rewards = setting?.RewardsAlloc ?? 0.10m,
                targetStabilityFund = setting?.TargetStabilityFund ?? 0m,
                stabilityOverflowRedirect = setting?.StabilityOverflowRedirect ?? ""
            }
            : null;

        var requestedCycles = targetSelection.Cycles
            .Select(c => new
            {
                month = FinancialConstants.MonthAbbreviations[c.MonthIndex - 1],
                year = c.Year,
                label = CategoryAttributionService.GetCycleRange(c.Year, c.MonthIndex, cycleDay).label,
                hasTransactions = allTransactions.Any(t => IsInCycle(t, c, cycleDay))
            })
            .ToList();

        // Outgoing conversation state: structured references the client echoes back next turn.
        // Matched transaction ids are the rows the DB actually returned this turn (re-derived,
        // never the client's claimed ids), capped, and only when this was a matching-row query.
        var matchedIds = queryPlan.TransactionData == TransactionDataLevel.MatchingRows && !string.IsNullOrWhiteSpace(queryPlan.SearchText)
            ? allTransactions.Take(MaxStateMatchedIds).Select(t => t.Id).ToList()
            : null;
        var resolvedCycleLabel = targetSelection.Cycles.Count > 0
            ? $"{FinancialConstants.MonthAbbreviations[targetSelection.Cycles[0].MonthIndex - 1]} {targetSelection.Cycles[0].Year}"
            : null;
        int? resolvedWishlistItemId = wishlistRows.Count == 1
            ? wishlistRows[0].Id
            : wishlistRows.Count(w => w.IsActive && !w.IsPurchased) == 1
                ? wishlistRows.First(w => w.IsActive && !w.IsPurchased).Id
                : null;
        // Resolve this turn's frame dimensions so the next follow-up can inherit them. Each falls
        // back to the base (prior) value when not in play this turn, giving "sticky" context that a
        // later turn's explicit value overrides (and read-side family scoping stops an incompatible
        // turn from consuming it).
        var turnCycleKeys = targetSelection.Cycles.Count > 0
            ? targetSelection.Cycles.Select(FormatCycleKey).ToList()
            : null;
        var turnThreshold = TryParseAmountThreshold(queryPlan.QueryText) is { } parsedThreshold
            ? ToWireThreshold(parsedThreshold)
            : null;
        var turnExactDate = exactDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var turnTransactionType = DetectTransactionType(queryPlan.QueryText);
        var isRecurringTurn = intentPlan.Intents.Any(i => i is AiIntent.RecurringList or AiIntent.RecurringUpcoming or AiIntent.RecurringAdd or AiIntent.RecurringEdit);
        var isWishlistTurn = intentPlan.Intents.Any(i => i is AiIntent.WishlistList or AiIntent.WishlistForecast or AiIntent.WishlistAdd or AiIntent.WishlistEdit);
        var isTransactionalTurn = !isRecurringTurn && !isWishlistTurn;
        var turnFacets = DetectQueryFacets(queryPlan.QueryText).Where(KnownQueryFacets.Contains).Take(12).ToList();
        var turnIntentNames = intentPlan.Intents.Select(ToIntentName).ToList();
        var turnTopic = DetermineConversationTopic(queryPlan.QueryText, turnIntentNames, intentPlan.ConversationState)
            ?? intentPlan.ConversationState.LastTopic;
        var turnRecurringStatus = DetectRecurringStatus(queryPlan.QueryText);
        var turnWishlistStatus = DetectWishlistStatus(queryPlan.QueryText);
        var turnLedgerCategory = intentPlan.Entities?.LedgerCategory ?? DetectLedgerCategory(queryPlan.QueryText);
        var turnForecast = TryParseLedgerBalanceForecast(queryPlan.QueryText);
        var mentionedRecurringReference = FindMentionedEntityName(queryPlan.QueryText, recurringRows.Select(r => r.Name));
        var mentionedWishlistReference = FindMentionedEntityName(queryPlan.QueryText, wishlistRows.Select(w => w.Name));
        // A threshold-only question ("which transaction exceeded 100") has no searchText, so its
        // matching rows weren't captured above -- capture them here so a referential follow-up
        // ("which of those was the biggest?") can resolve against them.
        var thresholdMatchedIds = matchedIds == null && !sensitiveMode && ToInternalThreshold(turnThreshold) is { } activeThreshold
            ? allTransactions
                .Where(t => t.Amount < 0 && !IsTransfer(t) && MatchesThreshold(Math.Abs(t.Amount), activeThreshold))
                .Take(MaxStateMatchedIds).Select(t => t.Id).ToList()
            : null;
        if (thresholdMatchedIds is { Count: 0 }) thresholdMatchedIds = null;
        // Filter-lifting follow-ups drop the sticky exclusions instead of carrying them forever.
        // (queryPlan.QueryText contains the original message; expansion only appends, so these
        // phrases are still detectable and the appended canonical clauses never trip them.)
        var clearsFilters = WantsClearFilters(queryPlan.QueryText);
        var includesTransfers = WantsIncludeTransfers(queryPlan.QueryText);
        var clearsAmountFilter = WantsClearAmountFilter(queryPlan.QueryText);
        var clearsSearch = WantsClearSearch(queryPlan.QueryText);
        var baseState = intentPlan.ConversationState;
        var outgoingState = baseState with
        {
            LastSearchText = clearsSearch ? null : queryPlan.SearchText ?? baseState.LastSearchText,
            LastCycleHint = queryPlan.CycleHint ?? baseState.LastCycleHint,
            LastResolvedCycle = resolvedCycleLabel ?? baseState.LastResolvedCycle,
            LastMatchedTransactionIds = matchedIds ?? thresholdMatchedIds ?? baseState.LastMatchedTransactionIds,
            LastWishlistItemId = queryPlan.WishlistItemId.HasValue
                ? resolvedWishlistItemId
                : resolvedWishlistItemId ?? baseState.LastWishlistItemId,
            LastResolvedCycleKeys = turnCycleKeys ?? baseState.LastResolvedCycleKeys,
            LastAmountThreshold = clearsAmountFilter ? null : turnThreshold ?? baseState.LastAmountThreshold,
            LastExcludeTransfers = !includesTransfers && (constraints.ExcludeTransfers || baseState.LastExcludeTransfers),
            LastExcludedCategories = clearsFilters
                ? null
                : constraints.ExcludedCategories is { Count: > 0 }
                    ? constraints.ExcludedCategories
                    : baseState.LastExcludedCategories,
            LastIncludedCategories = clearsFilters
                ? null
                : constraints.IncludedCategories is { Count: > 0 }
                    ? constraints.IncludedCategories
                    : baseState.LastIncludedCategories,
            LastLedgerCategory = turnLedgerCategory ?? baseState.LastLedgerCategory,
            LastCategory = intentPlan.Entities?.Category ?? baseState.LastCategory,
            LastTransactionType = turnTransactionType ?? baseState.LastTransactionType,
            LastExactDate = isTransactionalTurn ? turnExactDate : baseState.LastExactDate,
            LastComparison = isTransactionalTurn ? queryPlan.NeedsCycleComparison : baseState.LastComparison,
            LastRecurringReference = isRecurringTurn
                ? mentionedRecurringReference ?? queryPlan.SearchText ?? baseState.LastRecurringReference
                : baseState.LastRecurringReference,
            LastWishlistReference = isWishlistTurn
                ? mentionedWishlistReference ?? baseState.LastWishlistReference
                : baseState.LastWishlistReference,
            LastTopic = turnTopic,
            LastQueryFacets = turnFacets.Count > 0 ? turnFacets : null,
            LastRecurringStatus = isRecurringTurn ? turnRecurringStatus : baseState.LastRecurringStatus,
            LastWishlistStatus = isWishlistTurn ? turnWishlistStatus : baseState.LastWishlistStatus,
            LastTargetAmount = isTransactionalTurn ? turnForecast?.Target : baseState.LastTargetAmount
        };

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
        if (queryPlan.NeedsRecurring)
        {
            datasetStates[AiDatasetKey.Recurring] = new AiDatasetState(AiDatasetStatus.Available);
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
            && cycleTotalRecoverable;
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
        var resolution = ToResolution(intentPlan);

        // Built after recovery so a per-cycle exact outflow (when recovered) replaces the
        // truncated sample sum in both the single-cycle summary and the multi-cycle comparison.
        var cycleSummaries = queryPlan.NeedsCycleSummary
            ? BuildCycleSummaries(allTransactions, targetSelection.Cycles, cycleDay, !sensitiveMode, perCycleRecoveredOutflow)
            : new List<object>();
        object? balanceSnapshot = null;
        if (!sensitiveMode && Regex.IsMatch(queryPlan.QueryText, @"\b(wallet balance|ledger (?:category )?(?:balance|balances)|most balance|highest balance|balance right now)\b", RegexOptions.IgnoreCase))
        {
            var opening = await new CycleBalanceService(_context).GetOpeningBalanceAsync(selectedYear, selectedMonthIndex, cycleDay);
            var activeTransactions = allTransactions.Where(t => IsInCycle(t, new CycleKey(selectedYear, selectedMonthIndex), cycleDay)).ToList();
            decimal Net(string category) => activeTransactions.Sum(t => CategoryAttributionService.GetCategoryAmount(new Models.Transaction
            {
                Amount = t.Amount,
                LedgerCategory = t.LedgerCategory
            }, category));
            var balances = new[]
            {
                new { ledgerCategory = "Essentials", balance = opening.essentials + Net("Essentials") },
                new { ledgerCategory = "Growth", balance = opening.growth + Net("Growth") },
                new { ledgerCategory = "Stability", balance = opening.stability + Net("Stability") },
                new { ledgerCategory = "Rewards", balance = opening.rewards + Net("Rewards") }
            };
            balanceSnapshot = new
            {
                walletBalance = balances.Where(b => b.ledgerCategory != "Growth").Sum(b => b.balance),
                ledgerBalances = balances,
                highestLedgerBalance = balances.OrderByDescending(b => b.balance).First()
            };
        }
        // Per-cycle bill status (Paid/Pending/Discarded) -- the only path that sees discarded
        // recurring charges, which every other loader excludes. Triggered when a recurring
        // question also mentions a status word ("discarded", "skipped", "unpaid", "paid",
        // "pending"). Scoped to the requested cycles, defaulting to the active cycle.
        object? recurringBillStatus = null;
        if (queryPlan.NeedsRecurring &&
            Regex.IsMatch(queryPlan.QueryText, @"\b(discard|discarded|skip|skipped|unpaid|not paid|missed|paid|pending|overdue|due|status)\b", RegexOptions.IgnoreCase))
        {
            var statusCycles = targetSelection.Cycles.Count > 0
                ? targetSelection.Cycles
                : [new CycleKey(selectedYear, selectedMonthIndex)];
            var statuses = await LoadRecurringBillStatusesAsync(statusCycles, cycleDay, cancellationToken);
            recurringBillStatus = statuses
                .Select(s => sensitiveMode
                    ? (object)new { s.Name, s.Category, s.LedgerCategory, s.DueDate, s.Status }
                    : new { s.Name, s.Category, s.LedgerCategory, s.DueDate, s.Status, s.Amount })
                .ToList();
        }

        // Upcoming bills: the recurring.upcoming intent declared this metric but nothing produced
        // it. Active payments sorted by their stored NextDueDate so "when is my next bill / what's
        // coming up" is answerable, including each bill's frequency.
        object? recurringUpcoming = null;
        if (queryPlan.Metrics.Contains(DerivedMetric.RecurringUpcoming) && recurringRows.Count > 0)
        {
            recurringUpcoming = recurringRows
                .Where(r => r.Active)
                .OrderBy(r => DateTime.TryParse(r.NextDueDate, out var due) ? due : DateTime.MaxValue)
                .Take(10)
                .Select(r => sensitiveMode
                    ? (object)new { r.Name, r.Category, r.LedgerCategory, r.Frequency, nextDueDate = r.NextDueDate }
                    : new { r.Name, r.Category, r.LedgerCategory, r.Frequency, nextDueDate = r.NextDueDate, amount = Math.Abs(r.Amount) })
                .ToList();
        }

        // Frequency-normalized recurring cost ("how much do subscriptions cost me a month/year").
        // Amount-based, so only outside sensitive mode.
        object? recurringCostSummary = null;
        if (queryPlan.NeedsRecurring && !sensitiveMode && recurringRows.Count > 0 && WantsRecurringCostSummary(queryPlan.QueryText))
        {
            recurringCostSummary = BuildRecurringCostSummary(recurringRows);
        }

        var extraMetrics = new Dictionary<string, object?>();
        if (recurringUpcoming != null) extraMetrics["upcomingBills"] = recurringUpcoming;
        if (stabilityProgress != null) extraMetrics["stabilityProgress"] = stabilityProgress;
        if (affordableWishlistCount != null) extraMetrics["affordableWishlistCount"] = affordableWishlistCount;
        if (ledgerBalanceForecast != null) extraMetrics["ledgerBalanceForecast"] = ledgerBalanceForecast;
        if (recurringCostSummary != null) extraMetrics["recurringCostSummary"] = recurringCostSummary;

        var derivedMetrics = BuildDerivedMetrics(queryPlan, allTransactions, targetSelection.Cycles, cycleDay, sensitiveMode, exactMatchCount, perCycleRecoveredOutflow, balanceSnapshot, recurringBillStatus, extraMetrics);

        var context = new AiContext(
            Currency: setting?.Currency ?? "USD",
            Today: DateTime.Now.ToString("yyyy-MM-dd"),
            SensitiveMode: sensitiveMode,
            ActiveCycle: new
            {
                month = selectedMonth,
                year = selectedYear,
                label = CategoryAttributionService.GetCycleRange(selectedYear, selectedMonthIndex, cycleDay).label
            },
            Categories: categories,
            LedgerCategories: LedgerCategories,
            RequestedCycles: requestedCycles,
            DataScope: new
            {
                targetWasExplicit = targetSelection.ExplicitlyRequested,
                aggregatesCoverAllTransactionsInRequestedCycles = targetSelection.Cycles.Count > 0 && !scopeTruncated,
                aggregatesTruncated = scopeTruncated,
                // Present only when the sample was truncated but the database returned the exact
                // headline outflow: use this figure, not the partial sample sum, and do not hedge.
                recoveredExactOutflow = recoveredOutflow,
                detailedTransactionsIncluded = queryPlan.NeedsTransactionDetail ? Math.Min(allTransactions.Count, 120) : 0,
                detailedTransactionsTotalInScope = allTransactions.Count
            },
            IntentNames: intentNames,
            IntentResolution: resolution == null
                ? null
                : new
                {
                    intents = resolution.Intents.Select(i => i.ToString()).ToList(),
                    confidence = resolution.Confidence,
                    usedClassifier = resolution.UsedClassifier,
                    ambiguities = resolution.Ambiguities
                },
            Sufficiency: new
            {
                canAnswer = sufficiencyResult.CanAnswer,
                isApproximate = sufficiencyResult.IsApproximate,
                missing = sufficiencyResult.Missing.Select(m => new { dataset = m.DatasetKey, reason = m.Reason }).ToList(),
                recoverable = sufficiencyResult.Recoverable.Select(m => new { dataset = m.DatasetKey, reason = m.Reason }).ToList()
            },
            QueryPlan: new
            {
                transactionData = queryPlan.TransactionData.ToString(),
                    metrics = queryPlan.Metrics.Select(metric => metric.ToString()).ToList(),
                    searchText = queryPlan.SearchText,
                    cycleHint = queryPlan.CycleHint,
                    operations = turnFacets,
                    exactDate = turnExactDate,
                    transactionType = turnTransactionType,
                    ledgerCategory = turnLedgerCategory,
                    recurringStatus = turnRecurringStatus,
                    wishlistStatus = turnWishlistStatus
                },
            ConversationState: outgoingState,
            Constraints: constraints == AiConstraints.None
                ? null
                : new
                {
                    preventNavigation = constraints.PreventNavigation,
                    excludeTransfers = constraints.ExcludeTransfers,
                    excludedCategories,
                    excludedLedgerCategories,
                    includedCategories,
                    includedLedgerCategories,
                    hypothetical = constraints.Hypothetical
                },
            DerivedMetrics: derivedMetrics,
            CycleSummaries: cycleSummaries,
            RecentTransactions: recentTransactions,
            RecurringPayments: recurringContext,
            WishlistItems: wishlistContext,
            BudgetTargets: budgetTargets,
            WishlistForecast: wishlistForecast);
        var missing = sufficiencyResult.Missing.Select(m => m.DatasetKey).ToList();
        var sufficiency = new ContextSufficiency(
            Complete: sufficiencyResult.CanAnswer,
            // A truncated scope forces approximate wording UNLESS the exact figure was recovered
            // from the database (SQL SUM), in which case the headline number is precise.
            Approximate: sufficiencyResult.IsApproximate || (scopeTruncated && !recoveredOutflow.HasValue),
            Missing: missing);
        return new AiContextBuildResult(context, targetSelection.Cycles, cycleDay, selectedYear, sufficiency, outgoingState);
    }

    private static object BuildDerivedMetrics(
        AiQueryPlan queryPlan,
        IReadOnlyList<AiTransactionRow> transactions,
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        bool sensitiveMode,
        int? exactMatchCount,
        IReadOnlyDictionary<CycleKey, decimal>? perCycleRecoveredOutflow,
        object? balanceSnapshot = null,
        object? recurringBillStatus = null,
        IReadOnlyDictionary<string, object?>? extraMetrics = null)
    {
        var metrics = new Dictionary<string, object?>();
        if (recurringBillStatus != null) metrics["recurringBillStatus"] = recurringBillStatus;
        if (extraMetrics != null)
        {
            foreach (var kv in extraMetrics) metrics[kv.Key] = kv.Value;
        }
        if (queryPlan.Metrics.Contains(DerivedMetric.ActivityCount) || queryPlan.Metrics.Contains(DerivedMetric.MerchantMatches))
        {
            metrics["transactionMatches"] = new
            {
                query = queryPlan.SearchText,
                count = exactMatchCount ?? transactions.Count,
                totalOutflow = sensitiveMode ? (decimal?)null : Math.Abs(transactions.Where(t => t.Amount < 0 && !IsTransfer(t)).Sum(t => t.Amount)),
                complete = queryPlan.TransactionData == TransactionDataLevel.MatchingRows && (exactMatchCount.HasValue || transactions.Count < MaxTransactionsPerRange),
                    rows = sensitiveMode
                        ? transactions.Take(120).Select(t => (object)new { t.Id, t.Date, t.Description, t.Category }).ToList()
                        : transactions.Take(120).Select(t => (object)new { t.Id, t.Date, t.Description, t.Category, t.Amount }).ToList()
            };
        }
        if (balanceSnapshot != null) metrics["balanceSnapshot"] = balanceSnapshot;
        // Amount-threshold filter ("which transaction exceeded 250", "purchases over 100",
        // "anything between 50 and 200"). Amount-based, so suppressed in sensitive mode like the
        // other magnitude metrics.
        var threshold = TryParseAmountThreshold(queryPlan.QueryText);
        if (!sensitiveMode && threshold != null)
        {
            metrics["thresholdMatches"] = BuildThresholdMatches(
                transactions, threshold, sampleWasComplete: transactions.Count < MaxTransactionsPerRange);
        }
        if (Regex.IsMatch(queryPlan.QueryText, @"\b(which|what) day\b.*\b(most|highest|largest)\b|\bmost\b.*\b(day|daily)\b", RegexOptions.IgnoreCase))
        {
            var daily = transactions.Where(t => !IsTransfer(t))
                .GroupBy(t => t.Date)
                .Select(g => new
                {
                    date = g.Key,
                    inflow = g.Where(t => t.Amount > 0).Sum(t => t.Amount),
                    outflow = Math.Abs(g.Where(t => t.Amount < 0).Sum(t => t.Amount))
                }).ToList();
            metrics["dailyExtremes"] = sensitiveMode || daily.Count == 0 ? null : new
            {
                highestInflowDay = daily.OrderByDescending(d => d.inflow).First(),
                highestOutflowDay = daily.OrderByDescending(d => d.outflow).First()
            };
        }
        if (queryPlan.TransactionIds.Count > 0)
        {
            var referenced = transactions
                .Where(t => queryPlan.TransactionIds.Contains(t.Id, StringComparer.Ordinal))
                .ToList();
            var outflows = referenced.Where(t => t.Amount < 0 && !IsTransfer(t)).ToList();
            var highest = outflows.OrderByDescending(t => Math.Abs(t.Amount)).FirstOrDefault();
            metrics["referencedTransactions"] = new
            {
                ids = referenced.Select(t => t.Id).ToList(),
                count = referenced.Count,
                totalCost = Math.Abs(outflows.Sum(t => t.Amount)),
                highest = highest == null ? null : new
                {
                    highest.Id,
                    highest.Description,
                    highest.Date,
                    amount = sensitiveMode ? (decimal?)null : Math.Abs(highest.Amount)
                },
                complete = referenced.Count == queryPlan.TransactionIds.Count
            };
        }
        if (queryPlan.Metrics.Contains(DerivedMetric.CycleComparison))
        {
            metrics["cycleComparison"] = BuildCycleComparison(transactions, cycles, cycleDay, perCycleRecoveredOutflow);
        }
        if (queryPlan.Metrics.Contains(DerivedMetric.AnomalyDetection))
        {
            metrics["anomalies"] = DetectAnomalies(transactions)
                .Select(a => sensitiveMode
                    ? (object)new { a.Id, a.Description, a.Category, amount = (decimal?)null, a.Score, a.Reason }
                    : new { a.Id, a.Description, a.Category, amount = (decimal?)a.Amount, a.Score, a.Reason })
                .ToList();
        }
        if (queryPlan.Metrics.Contains(DerivedMetric.DuplicateDetection))
        {
            metrics["duplicateCandidates"] = DetectDuplicates(transactions)
                .Select(d => sensitiveMode
                    ? (object)new { d.Ids, d.Description, d.DaysApart, d.Confidence, d.Reasons, amount = (decimal?)null }
                    : new { d.Ids, d.Description, d.DaysApart, d.Confidence, d.Reasons, amount = (decimal?)d.Amount })
                .ToList();
        }
        return metrics;
    }

    private static object BuildCycleComparison(
        IReadOnlyList<AiTransactionRow> transactions,
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        IReadOnlyDictionary<CycleKey, decimal>? perCycleRecoveredOutflow)
    {
        return cycles.Select(cycle =>
        {
            var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
            var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start));
            var end = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end));
            var txs = transactions.Where(t => t.Timestamp >= start && t.Timestamp < end && !IsTransfer(t)).ToList();
            // Prefer the recovered exact SUM for this specific cycle when the sample was
            // truncated; otherwise the in-memory sample sum is already complete.
            var recovered = perCycleRecoveredOutflow != null && perCycleRecoveredOutflow.ContainsKey(cycle);
            var outflow = recovered ? perCycleRecoveredOutflow![cycle] : Math.Abs(txs.Where(t => t.Amount < 0).Sum(t => t.Amount));
            return new
            {
                month = FinancialConstants.MonthAbbreviations[cycle.MonthIndex - 1],
                year = cycle.Year,
                inflow = txs.Where(t => t.Amount > 0).Sum(t => t.Amount),
                outflow,
                net = txs.Where(t => t.Amount > 0).Sum(t => t.Amount) - outflow,
                exactOutflow = recovered
            };
        }).ToList();
    }

    private static List<object> BuildCycleSummaries(
        IReadOnlyList<AiTransactionRow> transactions,
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        bool includeAmounts,
        IReadOnlyDictionary<CycleKey, decimal>? perCycleRecoveredOutflow)
    {
        var summaries = new List<object>();
        foreach (var cycle in cycles)
        {
            var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
            var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start));
            var end = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end));
            var txs = transactions
                .Where(t => t.Timestamp >= start && t.Timestamp < end)
                .ToList();
            var nonTransferTxs = txs.Where(t => !IsTransfer(t)).ToList();

            if (!includeAmounts)
            {
                summaries.Add(new
                {
                    month = FinancialConstants.MonthAbbreviations[cycle.MonthIndex - 1],
                    year = cycle.Year,
                    label = range.label,
                    transactionCount = txs.Count,
                    categoryCounts = txs
                        .GroupBy(t => t.Category)
                        .Select(g => new { category = g.Key, count = g.Count() })
                        .OrderByDescending(x => x.count)
                        .Take(8)
                        .ToList(),
                    ledgerCounts = txs
                        .GroupBy(t => t.LedgerCategory)
                        .Select(g => new { ledgerCategory = g.Key, count = g.Count() })
                        .OrderByDescending(x => x.count)
                        .Take(8)
                        .ToList(),
                    recentTransactions = txs
                        .OrderByDescending(t => t.Timestamp)
                        .Take(12)
                        .Select(t => new
                        {
                            t.Id,
                            t.Date,
                            t.Description,
                            t.Category,
                            t.LedgerCategory,
                            txType = t.Amount < 0 ? "outflow" : t.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase) ? "transfer" : "inflow"
                        })
                        .ToList()
                });
                continue;
            }

            var categorySpend = nonTransferTxs
                .Where(t => t.Amount < 0)
                .GroupBy(t => t.Category)
                .Select(g => new { category = g.Key, outflow = Math.Abs(g.Sum(t => t.Amount)) })
                .OrderByDescending(x => x.outflow)
                .Take(8)
                .ToList();

            // Ledger net must be summed over ALL cycle transactions (transfers included), exactly
            // like the authoritative FinancialService/CycleBalanceService: GetCategoryAmount is
            // what routes a Transfer:Source->Target row into the -source/+target ledgers. Summing
            // over nonTransferTxs instead dropped every transfer, so ledgers funded by transfers
            // (typically Growth and Stability) wrongly showed a net of 0 / understated balances.
            var ledgerNet = LedgerCategories
                .Where(c => c != "Income")
                .Select(c => new
                {
                    ledgerCategory = c,
                        net = txs.Sum(t => CategoryAttributionService.GetCategoryAmount(new Models.Transaction
                    {
                        Amount = t.Amount,
                        LedgerCategory = t.LedgerCategory
                    }, c))
                })
                .ToList();

            var recoveredExact = perCycleRecoveredOutflow != null && perCycleRecoveredOutflow.ContainsKey(cycle);
            var income = nonTransferTxs.Where(t => t.Amount > 0).Sum(t => t.Amount);
            var outflow = recoveredExact ? perCycleRecoveredOutflow![cycle] : Math.Abs(nonTransferTxs.Where(t => t.Amount < 0).Sum(t => t.Amount));
            summaries.Add(new
            {
                month = FinancialConstants.MonthAbbreviations[cycle.MonthIndex - 1],
                year = cycle.Year,
                label = range.label,
                transactionCount = txs.Count,
                income,
                outflow,
                netChange = income - outflow,
                // True when this cycle's outflow/netChange came from an exact database SUM
                // rather than the (possibly truncated) in-memory sample.
                exactOutflow = recoveredExact,
                categorySpend,
                ledgerNet,
                largestTransactions = txs
                    .OrderByDescending(t => Math.Abs(t.Amount))
                    .Take(10)
                    .Select(t => new
                    {
                        t.Id,
                        t.Date,
                        t.Description,
                        t.Category,
                        t.LedgerCategory,
                        t.Amount,
                        txType = t.Amount < 0 ? "outflow" : t.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase) ? "transfer" : "inflow"
                    })
                    .ToList()
            });
        }
        return summaries;
    }

    private static bool IsTransfer(AiTransactionRow transaction) =>
        transaction.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase) ||
        transaction.Category.Equals("Transfer", StringComparison.OrdinalIgnoreCase);

    private static bool IsInCycle(AiTransactionRow transaction, CycleKey cycle, int cycleDay)
    {
        var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
        var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start));
        var end = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end));
        return transaction.Timestamp >= start && transaction.Timestamp < end;
    }

    private static IReadOnlyList<TransactionDateRange> MergeCycleRanges(IReadOnlyList<CycleKey> cycles, int cycleDay)
    {
        var ranges = cycles
            .Distinct()
            .Select(cycle => CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay))
            .Select(range => new TransactionDateRange(
                TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start)),
                TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end))))
            .OrderBy(range => range.Start)
            .ToList();
        if (ranges.Count <= 1) return ranges;

        var merged = new List<TransactionDateRange> { ranges[0] };
        foreach (var range in ranges.Skip(1))
        {
            var previous = merged[^1];
            if (range.Start <= previous.End)
            {
                merged[^1] = previous with { End = range.End > previous.End ? range.End : previous.End };
            }
            else
            {
                merged.Add(range);
            }
        }
        return merged;
    }

    private static TargetCycleSelection ResolveTargetCycles(
        string queryText,
        int selectedYear,
        int selectedMonthIndex,
        bool needsCycleSummary,
        bool needsComparison)
    {
        var explicitCycles = Regex.Matches(
                queryText,
                $@"\b(?<month>{MonthNamePattern})\s+(?<year>(?:19|20)\d{{2}})\b",
                RegexOptions.IgnoreCase)
            .Select(match => new CycleKey(
                int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture),
                GetMonthNumber(match.Groups["month"].Value)))
            .Distinct()
            .Take(12)
            .ToList();
        explicitCycles.AddRange(Regex.Matches(queryText, @"\b(?<year>(?:19|20)\d{2})-(?<month>0?[1-9]|1[0-2])\b")
            .Select(match => new CycleKey(
                int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture))));
        explicitCycles = explicitCycles.Distinct().Take(24).ToList();
        if (explicitCycles.Count > 0)
        {
            if (explicitCycles.Count == 2 &&
                Regex.IsMatch(queryText, @"\b(between|through|until|from)\b|\bto\b", RegexOptions.IgnoreCase))
            {
                explicitCycles = ExpandCycleRange(explicitCycles[0], explicitCycles[1], 24);
            }
            return new TargetCycleSelection(explicitCycles, true);
        }

        // "all cycles" / "every month" / "across all cycles" / "all-time": a search or
        // superlative that spans the user's whole history. Without this the query fell through
        // to the single active-cycle default, so "show badminton for all cycle" or "the most I
        // deposited into stability across all cycles" only ever looked at the current cycle.
        // Bounded to the trailing 24 cycles (they merge into one contiguous range) to stay
        // within the per-range row cap.
        if (Regex.IsMatch(queryText, @"\b(all|every|each)\s+(cycles?|months?)\b|\b(across|over|through(?:out)?|in)\s+all\b|\ball[- ]?time\b", RegexOptions.IgnoreCase))
        {
            return new TargetCycleSelection(
                Enumerable.Range(0, 24)
                    .Select(offset => AddMonths(selectedYear, selectedMonthIndex, -offset))
                    .Select(value => new CycleKey(value.Year, value.MonthIndex))
                    .ToList(),
                true);
        }

        var wholeYear = Regex.Match(
            queryText,
            @"\b(?:in|during|for|year)\s+(?<year>(?:19|20)\d{2})\b",
            RegexOptions.IgnoreCase);
        if (wholeYear.Success)
        {
            var year = int.Parse(wholeYear.Groups["year"].Value, CultureInfo.InvariantCulture);
            return new TargetCycleSelection(
                Enumerable.Range(1, 12).Select(month => new CycleKey(year, month)).ToList(),
                true);
        }

        if (Regex.IsMatch(queryText, @"\b(last|previous|prior)\s+year\b", RegexOptions.IgnoreCase))
        {
            return new TargetCycleSelection(
                Enumerable.Range(1, 12).Select(month => new CycleKey(selectedYear - 1, month)).ToList(),
                true);
        }
        if (Regex.IsMatch(queryText, @"\b(this|current)\s+year\b", RegexOptions.IgnoreCase))
        {
            return new TargetCycleSelection(
                Enumerable.Range(1, 12).Select(month => new CycleKey(selectedYear, month)).ToList(),
                true);
        }

        var quarter = Regex.Match(queryText,
            @"\b(?:q(?<number>[1-4])|(?<word>first|second|third|fourth) quarter)(?:\s+(?<year>(?:19|20)\d{2}))?\b",
            RegexOptions.IgnoreCase);
        if (quarter.Success)
        {
            var number = quarter.Groups["number"].Success
                ? int.Parse(quarter.Groups["number"].Value, CultureInfo.InvariantCulture)
                : quarter.Groups["word"].Value.ToLowerInvariant() switch
                {
                    "first" => 1, "second" => 2, "third" => 3, _ => 4
                };
            var year = quarter.Groups["year"].Success
                ? int.Parse(quarter.Groups["year"].Value, CultureInfo.InvariantCulture)
                : selectedYear;
            return new TargetCycleSelection(
                Enumerable.Range((number - 1) * 3 + 1, 3).Select(month => new CycleKey(year, month)).ToList(),
                true);
        }

        var relativeQuarter = Regex.Match(queryText, @"\b(?<which>this|current|last|previous|prior|next) quarter\b", RegexOptions.IgnoreCase);
        if (relativeQuarter.Success)
        {
            var activeOrdinal = selectedYear * 12 + selectedMonthIndex - 1;
            var activeQuarterStart = activeOrdinal - ((selectedMonthIndex - 1) % 3);
            var shift = relativeQuarter.Groups["which"].Value.ToLowerInvariant() switch
            {
                "last" or "previous" or "prior" => -3,
                "next" => 3,
                _ => 0
            };
            return new TargetCycleSelection(
                Enumerable.Range(activeQuarterStart + shift, 3)
                    .Select(ordinal => new CycleKey(ordinal / 12, ordinal % 12 + 1)).ToList(),
                true);
        }

        var cyclesAgo = Regex.Match(queryText, @"\b(?<count>\d{1,2})\s+(?:cycles?|months?)\s+ago\b", RegexOptions.IgnoreCase);
        if (cyclesAgo.Success)
        {
            var offset = -Math.Clamp(int.Parse(cyclesAgo.Groups["count"].Value, CultureInfo.InvariantCulture), 1, 120);
            var target = AddMonths(selectedYear, selectedMonthIndex, offset);
            return new TargetCycleSelection([new CycleKey(target.Year, target.MonthIndex)], true);
        }
        if (Regex.IsMatch(queryText, @"\b(?:cycle|month)\s+before\s+last\b", RegexOptions.IgnoreCase))
        {
            var target = AddMonths(selectedYear, selectedMonthIndex, -2);
            return new TargetCycleSelection([new CycleKey(target.Year, target.MonthIndex)], true);
        }

        var relativeCount = Regex.Match(
            queryText,
            @"\b(?:last|past|previous|prior)\s+(?<count>\d{1,2}|few)\s+(?:cycles?|months?)\b",
            RegexOptions.IgnoreCase);
        if (relativeCount.Success)
        {
            var count = relativeCount.Groups["count"].Value.Equals("few", StringComparison.OrdinalIgnoreCase)
                ? 3
                : Math.Clamp(int.Parse(relativeCount.Groups["count"].Value, CultureInfo.InvariantCulture), 1, 12);
            return new TargetCycleSelection(
                Enumerable.Range(1, count)
                    .Select(offset => AddMonths(selectedYear, selectedMonthIndex, -offset))
                    .Select(value => new CycleKey(value.Year, value.MonthIndex))
                    .ToList(),
                true);
        }

        var cycles = new List<CycleKey>();
        if (Regex.IsMatch(queryText, @"\b(this|current)\s+(cycle|month)\b", RegexOptions.IgnoreCase))
        {
            cycles.Add(new CycleKey(selectedYear, selectedMonthIndex));
        }
        if (Regex.IsMatch(queryText, @"\b(previous|prior|last)\s+(cycle|month)\b", RegexOptions.IgnoreCase))
        {
            var previous = AddMonths(selectedYear, selectedMonthIndex, -1);
            cycles.Add(new CycleKey(previous.Year, previous.MonthIndex));
            if (needsComparison) cycles.Add(new CycleKey(selectedYear, selectedMonthIndex));
        }
        if (Regex.IsMatch(queryText, @"\b(next|following)\s+(cycle|month)\b", RegexOptions.IgnoreCase))
        {
            var next = AddMonths(selectedYear, selectedMonthIndex, 1);
            cycles.Add(new CycleKey(next.Year, next.MonthIndex));
            if (needsComparison) cycles.Add(new CycleKey(selectedYear, selectedMonthIndex));
        }
        if (cycles.Count > 0) return new TargetCycleSelection(cycles.Distinct().ToList(), true);

        var namedMonth = Regex.Match(
            queryText,
            $@"(?:\b(?:cycle|month|in|for|about|during|and|vs\.?|versus|with|against)\s+|^\s*(?:(?:and|also|then|now|what about|how about|instead|just)\s+)?)(?<month>{MonthNamePattern})\b",
            RegexOptions.IgnoreCase);
        if (namedMonth.Success)
        {
            return new TargetCycleSelection(
                [new CycleKey(selectedYear, GetMonthNumber(namedMonth.Groups["month"].Value))],
                true);
        }

        if (!needsCycleSummary) return new TargetCycleSelection([], false);
        if (!needsComparison)
        {
            return new TargetCycleSelection([new CycleKey(selectedYear, selectedMonthIndex)], false);
        }

        return new TargetCycleSelection(
            Enumerable.Range(-6, 7)
                .Select(offset => AddMonths(selectedYear, selectedMonthIndex, offset))
                .Select(value => new CycleKey(value.Year, value.MonthIndex))
                .ToList(),
            false);
    }

    private static List<CycleKey> ExpandCycleRange(CycleKey first, CycleKey second, int maximum)
    {
        var firstOrdinal = first.Year * 12 + first.MonthIndex - 1;
        var secondOrdinal = second.Year * 12 + second.MonthIndex - 1;
        var start = Math.Min(firstOrdinal, secondOrdinal);
        var end = Math.Max(firstOrdinal, secondOrdinal);
        return Enumerable.Range(start, Math.Min(end - start + 1, maximum))
            .Select(ordinal => new CycleKey(ordinal / 12, ordinal % 12 + 1))
            .ToList();
    }

    private static (int Year, int MonthIndex) AddMonths(int year, int monthIndex, int offset)
    {
        var zeroBased = (monthIndex - 1) + offset;
        year += (int)Math.Floor(zeroBased / 12.0);
        var month = ((zeroBased % 12) + 12) % 12 + 1;
        return (year, month);
    }

    private async Task<AiChatResponse?> TryResolveLedgerEditAsync(
        string message,
        AiContext context,
        IReadOnlyList<CycleKey> targetCycles,
        int cycleDay,
        int defaultYear,
        IReadOnlyList<string>? referencedTransactionIds,
        CancellationToken cancellationToken)
    {
        var selectionFollowUp = referencedTransactionIds is { Count: > 0 } &&
            Regex.IsMatch(message, @"\b(alone|only|just|that one|this one|the one|the (?:first|second|third|fourth|last|former|latter|largest|biggest|smallest|cheapest|latest|earliest|pure|only) one|the former|the latter)\b", RegexOptions.IgnoreCase);
        if (!LooksLikeLedgerEditCommand(message) && !selectionFollowUp)
        {
            return null;
        }

        // defaultYear is threaded in from the already-loaded FinancialSetting during context
        // build, so this path no longer re-queries the settings row on every edit command.
        var hasReferencedIds = referencedTransactionIds is { Count: > 0 };
        var hasExactDate = TryExtractDate(message, defaultYear, out var targetDate, out var matchedDateText);
        if (!hasReferencedIds && !hasExactDate && targetCycles.Count == 0) return null;

        var searchText = hasReferencedIds && selectionFollowUp
            ? Regex.Replace(message, @"\b(the|one|alone|only|just|that|this|record|transaction|entry)\b", " ", RegexOptions.IgnoreCase).Trim()
            : hasReferencedIds ? null : ExtractLedgerEditSearchText(message, matchedDateText);
        if (!hasReferencedIds && string.IsNullOrWhiteSpace(searchText) && !hasExactDate)
        {
            return null;
        }

        if (context.SensitiveMode)
        {
            return new AiChatResponse("Unhide balances before using AI to edit ledger records.", []);
        }

        DateTime? scopeStart = null;
        DateTime? scopeEnd = null;
        string scopeLabel;
        if (hasReferencedIds)
        {
            scopeLabel = "for the referenced record";
        }
        else if (hasExactDate)
        {
            var dateStart = TransactionDate.StartOfDate(targetDate);
            var dateEnd = TransactionDate.ExclusiveEndOfDate(targetDate);
            scopeStart = dateStart;
            scopeEnd = dateEnd;
            scopeLabel = $"on {FormatDateForReply(targetDate)}";
        }
        else
        {
            var ranges = targetCycles
                .Select(cycle => CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay))
                .Select(range => new
                {
                    Start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start)),
                    End = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end)),
                    range.label
                })
                .ToList();
            var minStart = ranges.Min(range => range.Start);
            var maxEnd = ranges.Max(range => range.End);
            scopeStart = minStart;
            scopeEnd = maxEnd;
            scopeLabel = ranges.Count == 1 ? $"in {ranges[0].label}" : "in the requested cycles";
        }

        var matches = await FindLedgerEditMatchesAsync(
            searchText,
            referencedTransactionIds ?? [],
            scopeStart,
            scopeEnd,
            cancellationToken);

        // A selection follow-up ("the one named X", "that one") narrows a prior candidate set.
        // Free-form descriptors rarely substring-match a record's description, so if the text
        // filter eliminated everything, fall back to the referenced candidates themselves and let
        // the user pick, rather than falsely reporting "no matching record".
        if (matches.Count == 0 && selectionFollowUp && hasReferencedIds)
        {
            matches = await FindLedgerEditMatchesAsync(
                null,
                referencedTransactionIds!,
                scopeStart,
                scopeEnd,
                cancellationToken);
        }

        if (matches.Count == 0)
        {
            var targetDescription = string.IsNullOrWhiteSpace(searchText) ? "a ledger record" : $"a ledger record matching \"{searchText}\"";
            return new AiChatResponse($"I couldn't find {targetDescription} {scopeLabel}.", []);
        }

        if (matches.Count > 1)
        {
            var choices = string.Join(", ", matches.Select(match =>
                $"\"{match.Description}\" on {TransactionDate.ToDateOnly(match.Date):yyyy-MM-dd}"));
            // Carry the exact candidate ids so a follow-up ("the one named X", "that one", "the
            // second one") narrows against this same set. The query-plan path does not populate
            // matched ids for an edit intent, so the clarification attaches them explicitly.
            var clarificationState = new AiConversationState(
                LastIntent: "ledger.edit",
                LastSearchText: searchText,
                LastCycleHint: null,
                LastWishlistReference: null,
                LastResolvedCycle: null,
                LastMatchedTransactionIds: matches.Select(m => m.Id).ToList(),
                LastWishlistItemId: null,
                LastCategory: null);
            return new AiChatResponse(
                $"I found multiple matches {scopeLabel}: {choices}. Please specify which one to edit.",
                [],
                State: clarificationState);
        }

        var changes = ExtractRequestedLedgerChanges(message, context.Categories, context.LedgerCategories, defaultYear);
        var payload = new Dictionary<string, object?>
        {
            ["id"] = matches[0].Id,
            ["changes"] = changes
        };
        var changeReply = changes.Count > 0 ? " with your requested changes ready for review" : "";
        return new AiChatResponse($"Opened the \"{matches[0].Description}\" record {scopeLabel}{changeReply}.", [new AiUiAction("openEditLedgerDraft", payload)]);
    }

    private static bool LooksLikeLedgerEditCommand(string message)
    {
        var lower = message.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(lower, @"\b(edit|update|change|modify)\b", RegexOptions.IgnoreCase))
        {
            return false;
        }

        return !(lower.Contains("recurring") ||
            lower.Contains("subscription") ||
            lower.Contains("wishlist") ||
            lower.Contains("wish list") ||
            lower.Contains("goal"));
    }

    private static bool TryExtractDate(string message, int defaultYear, out DateOnly date, out string matchedText)
    {
        var isoMatch = Regex.Match(message, @"\b(?<year>\d{4})-(?<month>\d{1,2})-(?<day>\d{1,2})\b", RegexOptions.IgnoreCase);
        if (isoMatch.Success &&
            TryCreateDate(
                int.Parse(isoMatch.Groups["year"].Value, CultureInfo.InvariantCulture),
                int.Parse(isoMatch.Groups["month"].Value, CultureInfo.InvariantCulture),
                int.Parse(isoMatch.Groups["day"].Value, CultureInfo.InvariantCulture),
                out date))
        {
            matchedText = isoMatch.Value;
            return true;
        }

        const string monthPattern = "jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?";
        var monthDayMatch = Regex.Match(
            message,
            $@"\b(?<month>{monthPattern})\s+(?<day>\d{{1,2}})(?:st|nd|rd|th)?(?:,?\s+(?<year>\d{{4}}))?\b",
            RegexOptions.IgnoreCase);
        if (monthDayMatch.Success &&
            TryCreateDate(
                GetMonthNumber(monthDayMatch.Groups["month"].Value),
                monthDayMatch.Groups["day"].Value,
                monthDayMatch.Groups["year"].Success ? monthDayMatch.Groups["year"].Value : null,
                defaultYear,
                out date))
        {
            matchedText = monthDayMatch.Value;
            return true;
        }

        var dayMonthMatch = Regex.Match(
            message,
            $@"\b(?<day>\d{{1,2}})(?:st|nd|rd|th)?\s+(?<month>{monthPattern})(?:,?\s+(?<year>\d{{4}}))?\b",
            RegexOptions.IgnoreCase);
        if (dayMonthMatch.Success &&
            TryCreateDate(
                GetMonthNumber(dayMonthMatch.Groups["month"].Value),
                dayMonthMatch.Groups["day"].Value,
                dayMonthMatch.Groups["year"].Success ? dayMonthMatch.Groups["year"].Value : null,
                defaultYear,
                out date))
        {
            matchedText = dayMonthMatch.Value;
            return true;
        }

        date = default;
        matchedText = string.Empty;
        return false;
    }

    private static bool TryCreateDate(int month, string dayText, string? yearText, int defaultYear, out DateOnly date)
    {
        var year = string.IsNullOrWhiteSpace(yearText)
            ? defaultYear
            : int.Parse(yearText, CultureInfo.InvariantCulture);
        var day = int.Parse(dayText, CultureInfo.InvariantCulture);
        return TryCreateDate(year, month, day, out date);
    }

    private static bool TryCreateDate(int year, int month, int day, out DateOnly date)
    {
        try
        {
            date = new DateOnly(year, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            date = default;
            return false;
        }
    }

    private static int GetMonthNumber(string month)
    {
        return month[..3].ToLowerInvariant() switch
        {
            "jan" => 1,
            "feb" => 2,
            "mar" => 3,
            "apr" => 4,
            "may" => 5,
            "jun" => 6,
            "jul" => 7,
            "aug" => 8,
            "sep" => 9,
            "oct" => 10,
            "nov" => 11,
            "dec" => 12,
            _ => 0
        };
    }

    private static string ExtractLedgerEditSearchText(string message, string matchedDateText)
    {
        var withoutDate = string.IsNullOrWhiteSpace(matchedDateText)
            ? message
            : Regex.Replace(message, Regex.Escape(matchedDateText), " ", RegexOptions.IgnoreCase);
        withoutDate = Regex.Replace(withoutDate, $@"\b(?:{MonthNamePattern})(?:\s+(?:19|20)\d{{2}})?\b", " ", RegexOptions.IgnoreCase);
        withoutDate = Regex.Replace(withoutDate, @"\b(this|current|previous|prior|last|past)\s+(?:\d+\s+|few\s+)?(cycle|cycles|month|months)\b", " ", RegexOptions.IgnoreCase);
        withoutDate = Regex.Replace(withoutDate, @"\b(cycle|cycles|month|months)\b", " ", RegexOptions.IgnoreCase);
        var beforeChangeTarget = Regex.Split(withoutDate, @"\s+\b(to|into|as)\b\s+", RegexOptions.IgnoreCase)[0];
        var cleaned = Regex.Replace(
            beforeChangeTarget,
            @"\b(edit|update|change|modify|ledger|record|transaction|entry|on|at|in|for|from|please|can|you|the|my|a|an|and|with|amount|price|category|date|description)\b",
            " ",
            RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"[^\p{L}\p{N}\s'-]", " ");
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    private static Dictionary<string, object?> ExtractRequestedLedgerChanges(
        string message,
        IReadOnlyList<string> categories,
        IReadOnlyList<string> ledgerCategories,
        int defaultYear)
    {
        var changes = new Dictionary<string, object?>();

        var amountMatch = Regex.Match(
            message,
            @"\b(?:(?:amount|price|value|total|cost)\s*(?:to|as|=)\s*(?:[A-Z]{3}\s*)?[^\d-]*|to\s*(?:[A-Z]{3}\s*)?[\p{Sc}]?\s*)(?<amount>\d+(?:[.,]\d{1,2})?)\b",
            RegexOptions.IgnoreCase);
        if (amountMatch.Success &&
            decimal.TryParse(amountMatch.Groups["amount"].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            changes["amount"] = Math.Abs(amount);
        }

        var ledgerCategory = FindRequestedCanonicalValue(message, "ledger category", ledgerCategories);
        if (ledgerCategory != null) changes["ledgerCategory"] = ledgerCategory;
        var category = FindRequestedCanonicalValue(message, "category", categories, disallowPrefix: "ledger");
        if (category != null) changes["category"] = category;

        var txTypeMatch = Regex.Match(message, @"\b(?:type\s*)?(?:to|as|=)\s*(?<type>inflow|outflow|transfer)\b", RegexOptions.IgnoreCase);
        if (txTypeMatch.Success) changes["txType"] = txTypeMatch.Groups["type"].Value.ToLowerInvariant();

        var descriptionMatch = Regex.Match(
            message,
            @"\b(?:description|merchant|name)\s*(?:to|as|=)\s*[\""']?(?<value>[\p{L}\p{N}][\p{L}\p{N}\s&.'-]{0,100}?)[\""']?(?:\s+(?:and|with)\b|$)",
            RegexOptions.IgnoreCase);
        if (descriptionMatch.Success) changes["description"] = descriptionMatch.Groups["value"].Value.Trim();

        var dateChangeMatch = Regex.Match(message, @"\bdate\s*(?:to|as|=)\s*(?<date>.+)$", RegexOptions.IgnoreCase);
        if (dateChangeMatch.Success && TryExtractDate(dateChangeMatch.Groups["date"].Value, defaultYear, out var newDate, out _))
        {
            changes["date"] = newDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return changes;
    }

    private static string? FindRequestedCanonicalValue(
        string message,
        string fieldName,
        IReadOnlyList<string> allowed,
        string? disallowPrefix = null)
    {
        var match = Regex.Match(
            message,
            $@"\b(?<prefix>\w+\s+)?{Regex.Escape(fieldName)}\s*(?:to|as|=)\s*(?<value>[\p{{L}}\p{{N}}][\p{{L}}\p{{N}}\s&'-]{{0,100}})",
            RegexOptions.IgnoreCase);
        if (!match.Success ||
            (!string.IsNullOrWhiteSpace(disallowPrefix) && match.Groups["prefix"].Value.Trim().Equals(disallowPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var requested = match.Groups["value"].Value.Trim();
        return allowed
            .OrderByDescending(value => value.Length)
            .FirstOrDefault(value => requested.StartsWith(value, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatDateForReply(DateOnly date)
    {
        return date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
    }

    // Kept as a fixed, request-independent string (no interpolated data) so it forms an
    // identical prefix on every call -- see AiClient for why that matters for cost.
    // Per-request data (message/history/live app context) goes in BuildUserContent instead.
    private static string BuildSystemInstruction()
    {
        return @"You are FinancialApp AI for a personal finance application.

Rules:
- Only fulfill these capabilities: financial/cycle analysis, concise spending-improvement suggestions, app navigation, ledger filtering, opening add/edit drafts for ledger/recurring/wishlist, and ledger/wishlist/recurring Q&A.
- If outside scope, reply exactly or similarly: ""I'm unable to perform that action.""
- Never modify settings. Never create/update/delete ledger, wishlist, or recurring records. Draft/open modal only.
- Transaction creation means openAddLedgerDraft only; never save/send a transaction.
- Never directly toggle recurring active state. If the user asks to turn a recurring payment on or off, explain that AI cannot perform that direct toggle.
- Delete requests are unsupported. Reply that AI cannot delete records.
- If ambiguous about target record, category, cycle, action type, amount, or whether the user wants ledger vs recurring vs wishlist, ask one concise clarification with at most 3 questions, return no actions, and set closeChat false.
- Use only categories, ledger categories, cycles, and record ids from App context.
- requestedCycles is the server-resolved scope for named/relative cycles. Cycle summaries cover every transaction in those cycles; recentTransactions is only a bounded detail sample. Use cycleSummaries for totals and recentTransactions for identifying individual records.
- The server preloads context based on the question. If a relevant block is present, use it directly; never claim that the application lacks access to that data. An empty block is not the same as unavailable data: check dataScope and sensitiveMode.
- intents and queryPlan describe the server's query plan. sufficiency is authoritative; do not answer with invented numbers when a required dataset is incomplete.
- queryPlan and derivedMetrics are authoritative server outputs. Use derivedMetrics for counts, merchant/activity matches, comparisons, and forecasts; do not recount a bounded sample when a derived metric is present.
- conversationState contains structured references from the prior turn. Use it to resolve short follow-ups such as ""those"", ""the previous cycle"", or ""it"" before asking for clarification.
- Every transaction object has a txType field (""inflow"", ""outflow"", or ""transfer""). Positive amounts are inflows (income), negative amounts are outflows (expenses). When asked about expenses or finding the most expensive transactions, look only at transactions where txType is ""outflow"". Never treat inflows or transfers as expenses.
- If dataScope.aggregatesTruncated is true, the cycle aggregates cover only part of that cycle; say the totals are approximate rather than presenting them as complete.
- For count, frequency, or existence questions about an activity or description (for example ""badminton"", ""any TNG transactions"", ""show X across all cycles""), use derivedMetrics.transactionMatches.count and state the matching date range. A non-zero count means the records exist within the requested cycles; never answer ""none found"" when transactionMatches.count is greater than 0.
- A transaction's own date does NOT by itself identify its cycle. A cycle can span two calendar months (it runs from the cycle-start day of its labeled month to the day before the cycle-start day of the next month), so a transaction dated in June may belong to the cycle labeled May. The server has already assigned every transaction to the correct cycle in requestedCycles, cycleSummaries, and derivedMetrics. Never re-derive a transaction's cycle from its calendar month, and never relabel a requested cycle as ""current"" because its rows are dated in a later month.
- wishlistForecast is a server-calculated estimate that matches the app's Wishlist page. Its savingsPerCycle is the average positive Rewards saved per active cycle, remaining is the price minus the current Rewards balance, and estimatedTargetDate is already projected forward. Use its cycles, target date, savings rate, and status verbatim; do not invent a different arithmetic method or use total net savings. If sensitiveMode is true, explain that exact wishlist forecasting is hidden by privacy mode.
- budgetTargets holds the user's ledger allocation goals (fractions of income per ledger category) plus their stability-fund target. When analyzing spending or giving improvement advice, compare cycleSummaries.ledgerNet and categorySpend against budgetTargets and be specific about which ledger categories are over or under goal. If budgetTargets is absent (sensitiveMode or a non-analysis question), give general guidance without inventing target numbers.
- A requested cycle with hasTransactions=false is verified empty. An empty recentTransactions array alone does not prove there is no data unless dataScope says the target was explicit and the requested cycle is empty.
- If the user refers to an old or relative cycle, use requestedCycles rather than the active cycle. Never silently substitute the active or newest cycle.
- For openLedger targeting a requested cycle, copy its three-letter month and numeric year exactly from requestedCycles.
- For ledger edit requests, return openEditLedgerDraft when you can identify one exact transaction. Do not return openLedger just to search unless the user explicitly asks to show/filter/navigate.
- For edit drafts, put only the fields the user explicitly asked to change inside payload.changes. Never return an empty changes object when the user specified a change.
- If the user asks a question (for example ""how many"", ""what"", ""why"", ""compare"", ""analyze""), answer the question and return no actions unless the user explicitly asks to open/show/filter/navigate the ledger.
- If the user asks to see the complete transaction list for a cycle, use openLedger with that requested cycle instead of pretending the bounded recentTransactions sample is the complete list.
- If sensitiveMode is true, exact amounts/prices/balances are not available and must not be asked for or revealed. Refuse amount-specific questions briefly. Do not return edit actions in sensitiveMode.
- Use at most one action unless the user clearly asked for more.
- Set closeChat true only when the request is fully handled by a non-edit returned action and your reply contains no follow-up question. For edit actions, Q&A, analysis, rejected, or clarification replies, set closeChat false.
- Do not end replies with optional follow-up offers or questions like ""would you like a summary?"".
- Be concise: normally answer in 2-5 short sentences or at most 6 bullets. Never restate the entire context.
- balanceSnapshot is authoritative for wallet balance and current ledger balances. A cycle's netChange or ledgerNet is activity, not a balance.
- dailyExtremes is authoritative for the highest-inflow and highest-outflow day.
- For requests to sort, compare, recommend category combinations, or identify inactive/discarded subscriptions, answer the question only. Do not navigate unless explicitly asked to open or show a screen.
- derivedMetrics.recurringBillStatus is authoritative for per-cycle bill status. Each entry has status ""Paid"", ""Pending"", or ""Discarded"" for that cycle. A ""discarded"" or ""skipped"" bill is one with status ""Discarded"" -- this is distinct from a recurring payment being inactive (active=false). When asked which subscriptions were discarded/skipped/paid/pending/unpaid this cycle, use recurringBillStatus, not the active flag on recurringPayments.
- Each recurringPayments entry has a frequency (""Weekly"", ""Monthly"", or ""Annually"") and a nextDueDate. Use these to answer questions about billing cadence (""which subscriptions are billed annually"") or the next charge date of a specific bill.
- derivedMetrics.upcomingBills lists active recurring payments sorted by nextDueDate (soonest first), each with its frequency. Use it for ""when is my next bill"", ""what's coming up"", or ""what do I pay next"".
- derivedMetrics.stabilityProgress is authoritative for stability-fund progress: currentStabilityBalance, targetStabilityFund, percentReached, and remaining. Use it for ""am I on track for my stability fund"" / ""how close am I to my stability goal"". If targetStabilityFund is 0 the user has not set a target; say so rather than inventing a percentage.
- derivedMetrics.affordableWishlistCount is how many wishlist items your current Rewards balance can already cover. Use it for ""how many things on my wishlist can I afford now"".
- derivedMetrics.thresholdMatches is authoritative for amount-comparison questions (""which transaction exceeded 250"", ""purchases over 100"", ""anything between 50 and 200""). It already filtered outflows (transfers excluded) by the comparison: use its count, totalOutflow, and rows verbatim; never re-scan recentTransactions. If complete is false the sample was capped, so hedge. When it is absent (e.g. sensitiveMode), do not invent amounts.
- derivedMetrics.ledgerBalanceForecast answers ""how long until my <ledger> reaches <amount>"" for any ledger category. currentBalance is the ledger's balance now, remaining is target minus current, savingsPerCycle is the average positive per-cycle amount added to that ledger, and estimatedCycles/estimatedTargetDate are the projection. Use its status verbatim: already-reached, not-currently-reachable (savings rate <= 0), insufficient-cycle-data, or estimated-from-completed-cycles. Do not invent a different arithmetic method.
- derivedMetrics.recurringCostSummary is authoritative for total recurring/subscription cost. monthlyTotal and annualTotal are active recurring charges normalized to a common basis (weekly x52/12, annually /12), with perLedgerMonthly breaking it down by ledger category. Use it for ""how much do my subscriptions cost me a month/year""; do not sum recurringPayments yourself, since their cadences differ.
- Each wishlistItems entry that is purchased has a purchasedAt date. Use it to answer when a wishlist item was bought.

Allowed actions:
- openDashboard payload: { }
- openRecurring payload: { }
- openWishlist payload: { }
- openSettings payload: { }
- openLedger payload: { month, year, allCycles, category, ledgerCategory, txType, search, date }
- openAddLedgerDraft payload: { description, amount, txType, category, ledgerCategory, date }
- openAddRecurringDraft payload: { name, amount, category, ledgerCategory, startDate, endDate }
- openAddWishlistDraft payload: { name, price, priority, isActive }
- openEditLedgerDraft payload: { id, changes }
- openEditRecurringDraft payload: { id, changes }
- openEditWishlistDraft payload: { id, changes }";
    }

    private static string BuildUserContent(string message, IReadOnlyList<AiChatMessage> history, AiContext context)
    {
        // WhenWritingNull keeps optional blocks (e.g. budgetTargets on a non-analysis or
        // sensitive-mode turn) out of the prompt entirely rather than emitting a dead
        // "budgetTargets":null line on every request.
        var contextJson = JsonSerializer.Serialize(BuildAnswerContext(context), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        var historyJson = JsonSerializer.Serialize(history, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        return $@"User message: {JsonSerializer.Serialize(message)}
Recent chat JSON: {historyJson}
App context JSON: {contextJson}";
    }

    private AiChatResponse ParseAndValidateResponse(string text, AiContext context, string userMessage, AiConstraints constraints)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var reply = root.TryGetProperty("reply", out var replyProp) ? replyProp.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(reply)) reply = "I'm unable to perform that action.";

        var actions = new List<AiUiAction>();
        if (root.TryGetProperty("actions", out var actionsProp) && actionsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var actionEl in actionsProp.EnumerateArray().Take(3))
            {
                if (!actionEl.TryGetProperty("type", out var typeProp)) continue;
                var type = typeProp.GetString() ?? "";
                if (!AllowedActionTypes.Contains(type)) continue;
                var payload = actionEl.TryGetProperty("payload", out var payloadProp) && payloadProp.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<Dictionary<string, object?>>(payloadProp.GetRawText()) ?? []
                    : [];
                if (context.SensitiveMode && type.StartsWith("openEdit", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsQuestionOnlyRequest(userMessage) && type.Equals("openLedger", StringComparison.OrdinalIgnoreCase)) continue;
                // "Don't open the ledger" -> honor the negation deterministically; drop every
                // navigation action regardless of what the model chose to return.
                if (constraints.PreventNavigation && IsNavigationAction(type)) continue;
                if (context.SensitiveMode)
                {
                    RemoveSensitivePayloadFields(payload);
                }
                if (!IsActionSafe(type, payload, context)) continue;
                actions.Add(new AiUiAction(type, payload));
            }
        }

        var closeChat = false;
        if (actions.Count > 0 &&
            root.TryGetProperty("closeChat", out var closeChatProp) &&
            closeChatProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            closeChat = closeChatProp.GetBoolean() &&
                actions.All(action => !action.Type.StartsWith("openEdit", StringComparison.OrdinalIgnoreCase)) &&
                !LooksLikeFollowUp(reply);
        }

        return new AiChatResponse(reply, actions, closeChat);
    }

    private static void RemoveSensitivePayloadFields(Dictionary<string, object?> payload)
    {
        payload.Remove("amount");
        payload.Remove("price");
        if (payload.TryGetValue("changes", out var changesObj) && changesObj is JsonElement changesElement && changesElement.ValueKind == JsonValueKind.Object)
        {
            var changes = JsonSerializer.Deserialize<Dictionary<string, object?>>(changesElement.GetRawText()) ?? [];
            changes.Remove("amount");
            changes.Remove("price");
            payload["changes"] = changes;
        }
        else if (changesObj is Dictionary<string, object?> changes)
        {
            changes.Remove("amount");
            changes.Remove("price");
        }
    }

    private static readonly HashSet<string> NavigationActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openLedger", "openDashboard", "openRecurring", "openWishlist", "openSettings"
    };

    private static bool IsNavigationAction(string type) => NavigationActionTypes.Contains(type);

    private static bool IsQuestionOnlyRequest(string message)
    {
        var lower = message.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower)) return false;

        var asksQuestion = lower.Contains('?') ||
            lower.StartsWith("how ") ||
            lower.StartsWith("what ") ||
            lower.StartsWith("why ") ||
            lower.StartsWith("when ") ||
            lower.StartsWith("where ") ||
            lower.StartsWith("who ") ||
            lower.StartsWith("which ") ||
            lower.StartsWith("can you tell") ||
            lower.StartsWith("tell me") ||
            lower.StartsWith("analyze") ||
            lower.StartsWith("compare");
        if (!asksQuestion) return false;

        return !(lower.Contains("open ") ||
            lower.Contains("show me ") ||
            lower.Contains("go to ") ||
            lower.Contains("navigate") ||
            lower.Contains("filter") ||
            lower.Contains("apply filter") ||
            lower.Contains("take me"));
    }

    private static bool LooksLikeDeleteCommand(string message)
    {
        var lower = message.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower)) return false;

        // Word-boundary match (not just StartsWith) so phrasings like "please delete this"
        // or "can you remove that" are still caught by the deterministic refusal below,
        // rather than falling through to the model's own (best-effort, not guaranteed)
        // instruction-following for the same rule.
        if (!Regex.IsMatch(lower, @"\b(delete|remove|erase|cancel|discard)\b"))
        {
            return false;
        }

        return !(lower.StartsWith("what ") ||
            lower.StartsWith("which ") ||
            lower.StartsWith("why ") ||
            lower.StartsWith("how ") ||
            lower.Contains(" redundant") ||
            lower.Contains(" should i "));
    }

    private static bool LooksLikeFollowUp(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return false;
        if (reply.Contains('?')) return true;

        var lower = reply.ToLowerInvariant();
        return lower.Contains("clarify") ||
            lower.Contains("which ") ||
            lower.Contains("what ") ||
            lower.Contains("please choose") ||
            lower.Contains("please specify") ||
            lower.Contains("do you want") ||
            lower.Contains("not sure") ||
            lower.Contains("unclear");
    }

    private static bool IsActionSafe(string type, Dictionary<string, object?> payload, AiContext context)
    {
        if (!HasKnownOptionalString(payload, "category", context.Categories) ||
            !HasKnownOptionalString(payload, "ledgerCategory", context.LedgerCategories) ||
            !HasKnownOptionalString(payload, "txType", ["inflow", "outflow", "transfer"]) ||
            !HasKnownOptionalString(payload, "month", FinancialConstants.MonthAbbreviations) ||
            !HasValidOptionalInteger(payload, "year", 1900, 2100) ||
            !HasValidOptionalNonNegativeNumber(payload, "amount") ||
            !HasValidOptionalNonNegativeNumber(payload, "price") ||
            !HasValidOptionalIsoDate(payload, "date") ||
            !HasValidOptionalIsoDate(payload, "startDate") ||
            !HasValidOptionalIsoDate(payload, "endDate"))
        {
            return false;
        }

        if (payload.TryGetValue("changes", out var changesValue) && changesValue != null)
        {
            var changes = changesValue is JsonElement element && element.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText())
                : changesValue as Dictionary<string, object?>;
            if (changes == null ||
                !HasKnownOptionalString(changes, "category", context.Categories) ||
                !HasKnownOptionalString(changes, "ledgerCategory", context.LedgerCategories) ||
                !HasKnownOptionalString(changes, "txType", ["inflow", "outflow", "transfer"]) ||
                !HasValidOptionalNonNegativeNumber(changes, "amount") ||
                !HasValidOptionalNonNegativeNumber(changes, "price") ||
                !HasValidOptionalIsoDate(changes, "date") ||
                !HasValidOptionalIsoDate(changes, "startDate") ||
                !HasValidOptionalIsoDate(changes, "endDate"))
            {
                return false;
            }
        }

        if (type.Equals("openEditRecurringDraft", StringComparison.OrdinalIgnoreCase))
        {
            return HasKnownId(payload, "id", context.RecurringPayments);
        }
        if (type.Equals("openEditWishlistDraft", StringComparison.OrdinalIgnoreCase))
        {
            return HasKnownId(payload, "id", context.WishlistItems);
        }
        if (type.Equals("openEditLedgerDraft", StringComparison.OrdinalIgnoreCase))
        {
            return HasKnownId(payload, "id", context.RecentTransactions);
        }
        return true;
    }

    private static bool HasKnownOptionalString(
        IReadOnlyDictionary<string, object?> payload,
        string key,
        IReadOnlyCollection<string> allowed)
    {
        if (!payload.TryGetValue(key, out var value) || value == null) return true;
        var text = value is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : value as string;
        return !string.IsNullOrWhiteSpace(text) && allowed.Contains(text, StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasValidOptionalNonNegativeNumber(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value == null) return true;
        double number;
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Number)
        {
            if (!element.TryGetDouble(out number)) return false;
        }
        else if (!double.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }
        return double.IsFinite(number) && number >= 0;
    }

    private static bool HasValidOptionalInteger(
        IReadOnlyDictionary<string, object?> payload,
        string key,
        int minimum,
        int maximum)
    {
        if (!payload.TryGetValue(key, out var value) || value == null) return true;
        int number;
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Number)
        {
            if (!element.TryGetInt32(out number)) return false;
        }
        else if (!int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }
        return number >= minimum && number <= maximum;
    }

    private static bool HasValidOptionalIsoDate(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value == null) return true;
        var text = value is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : value as string;
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static bool HasKnownId(Dictionary<string, object?> payload, string key, object records)
    {
        if (!payload.TryGetValue(key, out var idObj) || idObj == null) return false;
        var id = idObj.ToString();
        if (string.IsNullOrWhiteSpace(id)) return false;
        var json = JsonSerializer.Serialize(records);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array &&
            doc.RootElement.EnumerateArray().Any(e =>
                (e.TryGetProperty("id", out var p) || e.TryGetProperty("Id", out p)) &&
                string.Equals(p.ToString(), id, StringComparison.OrdinalIgnoreCase));
    }

    internal sealed record CycleKey(int Year, int MonthIndex);

    private sealed record AiContext(
        string Currency,
        string Today,
        bool SensitiveMode,
        object ActiveCycle,
        IReadOnlyList<string> Categories,
        IReadOnlyList<string> LedgerCategories,
        object RequestedCycles,
        object DataScope,
        IReadOnlyList<string> IntentNames,
        object? IntentResolution,
        object Sufficiency,
        object QueryPlan,
        AiConversationState? ConversationState,
        object? Constraints,
        object DerivedMetrics,
        object CycleSummaries,
        object RecentTransactions,
        object RecurringPayments,
        object WishlistItems,
        object? BudgetTargets,
        object? WishlistForecast);
}
