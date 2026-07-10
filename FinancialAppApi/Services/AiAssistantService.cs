using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

// Wire contract carried back to the client on AiChatResponse and echoed on the next
// AiChatRequest. Holds only structured *references* (never amounts or full records) so short
// follow-ups ("those", "the previous cycle", "it") can be resolved. Treated as untrusted on
// the way back in (see AiAssistantService.SanitizeConversationState) -- IDs are re-derived
// against the DB, never trusted verbatim.
public sealed record AiConversationState(
    string? LastIntent,
    string? LastSearchText,
    string? LastCycleHint,
    string? LastWishlistReference,
    string? LastResolvedCycle = null,
    IReadOnlyList<string>? LastMatchedTransactionIds = null,
    int? LastWishlistItemId = null,
    string? LastCategory = null);

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
        if (string.IsNullOrWhiteSpace(message))
        {
            return Ok(new AiChatResponse("Please ask a financial question or tell me what you want to open.", []));
        }
        if (message.Length > MaxMessageLength)
        {
            return Ok(new AiChatResponse("That message is too long. Please shorten it and try again.", []));
        }
        if (LooksLikeDeleteCommand(message))
        {
            return Ok(new AiChatResponse("I'm unable to delete records. You can delete it manually from the app if sensitive mode is off.", []));
        }
        if (TryHandleSmallTalk(message, out var smallTalkResponse))
        {
            return Ok(smallTalkResponse!);
        }

        if (!_aiClient.IsConfigured)
        {
            return Ok(new AiChatResponse("AI chat is not configured on the server.", []));
        }

        var history = SanitizeHistory(request.History);
        var priorState = SanitizeConversationState(request.State);
        var intentPlan = ResolveDeterministically(message, history, priorState);
        if (intentPlan.Confidence < 0.72 || intentPlan.Intents.Contains(AiIntent.General))
        {
            var classified = await TryClassifyIntentAsync(message, history, cancellationToken);
            if (classified != null && classified.Confidence >= intentPlan.Confidence)
            {
                intentPlan = MergeResolutions(message, history, classified, priorState);
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
            return Ok(insufficient);
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
                return Ok(resolvedEdit);
            }
        }

        // Prior turns are still folded into the query plan above regardless (so a
        // multi-message conversation about the same topic keeps fetching the right data) --
        // this only controls whether the model literally sees the past dialogue text, which
        // it rarely needs for a long, self-contained question.
        var promptHistory = NeedsHistoryContext(message) ? history : [];
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
                    MaxOutputTokens: intentPlan.QueryPlan.NeedsCycleComparison ? 800 : 550,
                    SystemInstruction: systemInstruction,
                    ResponseJsonSchema: AiResponseSchemas.Chat,
                    ThinkingLevel: intentPlan.QueryPlan.NeedsCycleComparison ? "medium" : "low",
                    ModelConfigurationKey: "AiModels:Chat",
                    FallbackModelConfigurationKey: "AiFallbackModels:Chat"),
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
        @"^(and|also|what about|how about|what if)\b|\b(that|this|those|these|it|them|the other|other one|same)\b",
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
        return wordCount <= 5 || FollowUpSignal.IsMatch(trimmed);
    }

    private async Task<IntentClassification?> TryClassifyIntentAsync(
        string message,
        IReadOnlyList<AiChatMessage> history,
        CancellationToken cancellationToken)
    {
        var classifierPrompt = $"Classify the user's financial-app request. Return only the JSON schema. " +
            $"Choose one or more intents, extract searchText for a merchant/activity, and preserve cycle wording. " +
            $"User message: {JsonSerializer.Serialize(message)} " +
            $"Recent history: {JsonSerializer.Serialize(history.TakeLast(4))}";
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
                    ModelConfigurationKey: "AiModels:IntentClassifier",
                    FallbackModelConfigurationKey: "AiFallbackModels:IntentClassifier"),
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
                              i.Equals("ledger.merchant_search", StringComparison.OrdinalIgnoreCase))) return null;

        var countMatch = Regex.Match(message,
            @"\b(?:how many|how often|number of times)\s+(?<value>[\p{L}\p{N}][\p{L}\p{N}'& -]{1,50}?)\s+(?:did|do|does|have|has|i|we)\b",
            RegexOptions.IgnoreCase);
        if (countMatch.Success) return NormalizeSearchText(countMatch.Groups["value"].Value);

        var showMatch = Regex.Match(message,
            @"\b(?:show|find|search|latest|last)\s+(?<value>[\p{L}\p{N}][\p{L}\p{N}'& -]{1,50}?)\s+(?:spending|purchase|purchases|transactions?|payments?)\b",
            RegexOptions.IgnoreCase);
        if (showMatch.Success) return NormalizeSearchText(showMatch.Groups["value"].Value);

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

    private static string NormalizeSearchText(string value) =>
        Regex.Replace(value.Trim(), @"^(?:my|the)\s+", string.Empty, RegexOptions.IgnoreCase);

    private static AiConversationState ResolveConversationState(
        IReadOnlyList<AiChatMessage> history,
        IReadOnlyList<string> intents,
        string? searchText,
        string? cycleHint,
        AiConversationState? priorState = null)
    {
        var previousUserText = history.LastOrDefault(m => m.Role == "user")?.Content;
        var carriesTransactionState = Regex.IsMatch(
            previousUserText ?? string.Empty,
            @"\b(those|these|them|that one|the highest one|the previous one|all of those|which of those)\b",
            RegexOptions.IgnoreCase);
        return new AiConversationState(
            intents.FirstOrDefault(i => !i.Equals("general", StringComparison.OrdinalIgnoreCase)) ?? priorState?.LastIntent,
            searchText ?? ExtractLikelySearchText(previousUserText ?? string.Empty, intents) ?? ExtractConversationSearch(previousUserText) ?? priorState?.LastSearchText,
            cycleHint ?? ExtractConversationCycle(previousUserText) ?? priorState?.LastCycleHint,
            ExtractWishlistReference(previousUserText) ?? priorState?.LastWishlistReference,
            priorState?.LastResolvedCycle,
            carriesTransactionState ? priorState?.LastMatchedTransactionIds : null,
            carriesTransactionState ? priorState?.LastWishlistItemId : null,
            priorState?.LastCategory);
    }

    private static string? ExtractConversationSearch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, @"\b(?:for|from|at|about|on)\s+([\p{L}\p{N}][\p{L}\p{N}'& -]{1,60})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
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
        return new AiConversationState(
            intent,
            Clamp(state.LastSearchText),
            Clamp(state.LastCycleHint),
            Clamp(state.LastWishlistReference),
            Clamp(state.LastResolvedCycle),
            matchedIds is { Count: > 0 } ? matchedIds : null,
            state.LastWishlistItemId is > 0 ? state.LastWishlistItemId : null,
            Clamp(state.LastCategory));
    }

    private const string MonthNamePattern = "jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?";

    private static readonly Regex CycleComparisonSignal = new(
        @"\b(compare|comparison|vs\.?|versus|trend|history|historical|over time|each month|every month|past (few |\d+ )?months?|past (few |\d+ )?cycles?|year over year|month over month)\b",
        RegexOptions.Compiled);

    private static readonly Regex CycleAnalysisSignal = new(
        @"\b(spend|spent|spending|budget|income|outflow|inflow|balance|total|average|net|save|saved|savings|essentials|growth|stability|rewards|cycle|this month|last month|how much|money going|doing better|doing worse|afford|financial health|performance)\b",
        RegexOptions.Compiled);

    private static readonly Regex TransactionDetailSignal = new(
        @"\b(transaction|transactions|ledger|purchase|purchased|bought|paid|payment|receipt|charge|charged|expense|expenses|find|search|when did|did i|edit|update|change|modify|record|entry|merchant|cost me|how often|frequency)\b",
        RegexOptions.Compiled);

    // "how much / how many / total / average" questions are answered from the cycle summary
    // aggregates, so they never need the per-row detail block -- even though a phrase like
    // "how much did I spend" trips TransactionDetailSignal on the incidental "did i".
    private static readonly Regex AggregateQuestionSignal = new(
        @"\b(how much|how many|total|totals|average|averages|avg|breakdown|sum)\b",
        RegexOptions.Compiled);

    private static readonly Regex CountQuestionSignal = new(
        @"\b(how many|how often|number of times|times did i|played|visited|frequency)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ...unless the user explicitly asks to see the individual records. These words mean the
    // detail sample is genuinely wanted and override the aggregate suppression above.
    private static readonly Regex ExplicitRecordSignal = new(
        @"\b(which|show|list|find|search|each|when did|edit|update|change|modify|receipt|merchant|entry|entries)\b",
        RegexOptions.Compiled);

    private static readonly Regex RecurringSignal = new(
        @"\b(recurring|subscription|subscriptions|bill|bills|monthly payment|autopay|auto-pay)\b",
        RegexOptions.Compiled);

    private static readonly Regex WishlistSignal = new(
        @"\b(wishlist|wish list|want to buy|saving for|priority item|afford)\b",
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
        RecurringUpcoming
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
    internal sealed record AiWishlistRow(int Id, string Name, decimal Price, string Priority, bool IsActive, bool IsPurchased, DateTime CreatedAt);
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
        var targetSelection = ResolveTargetCycles(
            queryPlan.QueryText,
            selectedYear,
            selectedMonthIndex,
            queryPlan.NeedsCycleSummary,
            queryPlan.NeedsCycleComparison);
        if (queryPlan.NeedsWishlistForecast && !targetSelection.ExplicitlyRequested)
        {
            targetSelection = new TargetCycleSelection(
                Enumerable.Range(1, 3)
                    .Select(offset => AddMonths(selectedYear, selectedMonthIndex, -offset))
                    .Select(value => new CycleKey(value.Year, value.MonthIndex))
                    .ToList(),
                false);
        }

        object recurringContext = Array.Empty<object>();
        if (queryPlan.NeedsRecurring)
        {
            var recurring = await LoadRecurringRowsAsync(cancellationToken);
            recurringContext = sensitiveMode
                ? recurring.Select(r => new
                {
                    r.Id, r.Name, r.Category, r.LedgerCategory,
                    r.StartDate, r.EndDate, r.DueDate, r.Active
                }).ToList()
                : recurring;
        }

        object wishlistContext = Array.Empty<object>();
        var wishlistRows = new List<AiWishlistRow>();
        if (queryPlan.NeedsWishlist)
        {
            var wishlist = await LoadWishlistRowsAsync(queryPlan.WishlistItemId, cancellationToken);
            wishlistRows = wishlist;
            wishlistContext = sensitiveMode
                ? wishlist.Select(w => new
                {
                    w.Id, w.Name, w.Priority, w.IsActive, w.IsPurchased, w.CreatedAt
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

        // Phase 2: enforce scope exclusions deterministically before any aggregate, detail
        // sample, or derived metric is built -- "excluding rent" / "without transfers" must
        // remove those rows from every downstream number, not just be hinted to the model.
        var constraints = intentPlan.Constraints;
        var (excludedCategories, excludedLedgerCategories) = ResolveConstraintCategories(
            constraints.ExcludedCategories, categories, LedgerCategories);
        if (constraints.ExcludeTransfers || excludedCategories.Count > 0 || excludedLedgerCategories.Count > 0)
        {
            allTransactions = allTransactions.Where(t =>
                    !(constraints.ExcludeTransfers && IsTransfer(t)) &&
                    !excludedCategories.Contains(t.Category, StringComparer.OrdinalIgnoreCase) &&
                    !excludedLedgerCategories.Contains(t.LedgerCategory, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        // Whether an exact cycle total is even eligible to be recovered: SumOutflowAsync cannot
        // express category exclusions, so an "exact" figure that ignored them would lie.
        var cycleTotalRecoverable = targetSelection.Cycles.Count > 0
            && excludedCategories.Count == 0
            && excludedLedgerCategories.Count == 0;

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
        var wishlistForecast = queryPlan.NeedsWishlistForecast && !sensitiveMode
            ? BuildWishlistForecast(new WishlistForecastPolicy(
                    wishlistRows,
                    allTransactions,
                    targetSelection.Cycles,
                    cycleDay,
                    activeCycleStart,
                    wishlistReference))
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
        var baseState = intentPlan.ConversationState;
        var outgoingState = baseState with
        {
            LastSearchText = queryPlan.SearchText ?? baseState.LastSearchText,
            LastCycleHint = queryPlan.CycleHint ?? baseState.LastCycleHint,
            LastResolvedCycle = resolvedCycleLabel ?? baseState.LastResolvedCycle,
            LastMatchedTransactionIds = matchedIds ?? baseState.LastMatchedTransactionIds,
            LastWishlistItemId = queryPlan.WishlistItemId.HasValue
                ? resolvedWishlistItemId
                : resolvedWishlistItemId ?? baseState.LastWishlistItemId
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
        var derivedMetrics = BuildDerivedMetrics(queryPlan, allTransactions, targetSelection.Cycles, cycleDay, sensitiveMode, exactMatchCount, perCycleRecoveredOutflow);

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
                cycleHint = queryPlan.CycleHint
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
                    includedTerms = constraints.IncludedCategories,
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
        IReadOnlyDictionary<CycleKey, decimal>? perCycleRecoveredOutflow)
    {
        var metrics = new Dictionary<string, object?>();
        if (queryPlan.Metrics.Contains(DerivedMetric.ActivityCount) || queryPlan.Metrics.Contains(DerivedMetric.MerchantMatches))
        {
            metrics["transactionMatches"] = new
            {
                query = queryPlan.SearchText,
                count = exactMatchCount ?? transactions.Count,
                complete = queryPlan.TransactionData == TransactionDataLevel.MatchingRows && (exactMatchCount.HasValue || transactions.Count < MaxTransactionsPerRange),
                    rows = sensitiveMode
                        ? transactions.Take(120).Select(t => (object)new { t.Id, t.Date, t.Description, t.Category }).ToList()
                        : transactions.Take(120).Select(t => (object)new { t.Id, t.Date, t.Description, t.Category, t.Amount }).ToList()
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

            var ledgerNet = LedgerCategories
                .Where(c => c != "Income")
                .Select(c => new
                {
                    ledgerCategory = c,
                        net = nonTransferTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(new Models.Transaction
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
        if (cycles.Count > 0) return new TargetCycleSelection(cycles.Distinct().ToList(), true);

        var namedMonth = Regex.Match(
            queryText,
            $@"(?:\b(?:cycle|month|in|for|about)\s+)(?<month>{MonthNamePattern})\b",
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
        if (!LooksLikeLedgerEditCommand(message))
        {
            return null;
        }

        // defaultYear is threaded in from the already-loaded FinancialSetting during context
        // build, so this path no longer re-queries the settings row on every edit command.
        var hasReferencedIds = referencedTransactionIds is { Count: > 0 };
        var hasExactDate = TryExtractDate(message, defaultYear, out var targetDate, out var matchedDateText);
        if (!hasReferencedIds && !hasExactDate && targetCycles.Count == 0) return null;

        var searchText = hasReferencedIds ? null : ExtractLedgerEditSearchText(message, matchedDateText);
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

        if (matches.Count == 0)
        {
            var targetDescription = string.IsNullOrWhiteSpace(searchText) ? "a ledger record" : $"a ledger record matching \"{searchText}\"";
            return new AiChatResponse($"I couldn't find {targetDescription} {scopeLabel}.", []);
        }

        if (matches.Count > 1)
        {
            var choices = string.Join(", ", matches.Select(match =>
                $"\"{match.Description}\" on {TransactionDate.ToDateOnly(match.Date):yyyy-MM-dd}"));
            return new AiChatResponse($"I found multiple matches {scopeLabel}: {choices}. Please specify which one to edit.", []);
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
- For count/frequency questions about an activity or description (for example, badminton), use derivedMetrics.transactionMatches.count and state the matching date range.
- wishlistForecast is a server-calculated estimate. Use its cycles, target date, savings rate, and status; do not invent a different arithmetic method. If sensitiveMode is true, explain that exact wishlist forecasting is hidden by privacy mode.
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
