using FinancialAppApi.Database;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

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
        "openAddLedgerDraft",
        "openAddRecurringDraft",
        "openAddWishlistDraft",
        "openEditLedgerDraft",
        "openEditRecurringDraft",
        "openEditWishlistDraft",
        "requestDeleteLedger",
        "requestDeleteRecurring",
        "requestDeleteWishlist",
        "requestConfirmRecurringBill",
        "requestDiscardRecurringBill",
        "requestPurchaseWishlist",
        "requestUnpurchaseWishlist",
        "toggleRecurring",
        "updateRecurringReminder",
        "openLedgerExport"
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
    private readonly CategorySuggestionService? _categorySuggestionService;
    private readonly RecurringOccurrenceService _recurringOccurrenceService;
    private readonly FinancialClock _financialClock;
    private readonly ILogger<AiAssistantService> _logger;

    public AiAssistantService(
        AiClient aiClient,
        AppDbContext context,
        TransactionCategoryService categoryService,
        CategorySuggestionService? categorySuggestionService = null,
        RecurringOccurrenceService? recurringOccurrenceService = null,
        FinancialClock? financialClock = null,
        ILogger<AiAssistantService>? logger = null)
    {
        _logger = logger ?? NullLogger<AiAssistantService>.Instance;
        _aiClient = aiClient;
        _context = context;
        _categoryService = categoryService;
        _categorySuggestionService = categorySuggestionService;
        _recurringOccurrenceService = recurringOccurrenceService ??
            new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        _financialClock = financialClock ?? FinancialClock.Utc;
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
        if (context.SensitiveMode && LooksLikeProtectedMutationCommand(message))
        {
            return Ok(new AiChatResponse(
                "Sensitive mode prevents record changes. Unhide balances before editing, deleting, purchasing, confirming, discarding, or toggling a record.",
                [], State: contextResult.OutgoingState));
        }
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
        var systemInstruction = SystemInstruction;
        var userContent = BuildUserContent(message, promptHistory, context);
        var isLedgerAdd = intentPlan.Intents.Contains(AiIntent.LedgerAdd);
        var structuredLedgerDraftCount = isLedgerAdd ? CountLedgerDraftListRecords(message) : 0;

        string text;
        try
        {
            text = await _aiClient.GenerateTextAsync(
                [AiPart.FromText(userContent)],
                new AiGenerationOptions(
                    Feature: "chat",
                    Temperature: 0.15,
                    // Gemini 3 thinking tokens are drawn from this same budget, so a low ceiling
                    // let a normal analytic reply plus its thinking overflow into MAX_TOKENS (a 503
                    // "response too long and got cut off"). Give the visible JSON reply real
                    // headroom above the thinking it competes with.
                    MaxOutputTokens: intentPlan.Intents.Contains(AiIntent.LedgerAdd)
                        ? 3200
                        : intentPlan.QueryPlan.NeedsCycleComparison ? 1600 : 1200,
                    SystemInstruction: systemInstruction,
                    ResponseJsonSchema: isLedgerAdd
                        ? AiResponseSchemas.LedgerDraftChat(context.Categories, structuredLedgerDraftCount)
                        : AiResponseSchemas.Chat(
                            context.Categories,
                            includeLedgerFilters: WantsLedgerFilterControls(intentPlan),
                            includeReminderControls: WantsReminderControls(intentPlan)),
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
        // Enrichment is a best-effort second pass over an already-valid answer. Its own
        // provider call must never turn a complete reply into a 503, so it degrades to
        // the unenriched drafts instead of propagating.
        try
        {
            parsed = await EnrichLedgerDraftActionsAsync(parsed, message, context, cancellationToken);
        }
        catch (Exception ex) when (ex is AiClientException or JsonException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Ledger draft enrichment failed; returning unenriched drafts.");
        }
        // Phase 5: an incomplete aggregate must never surface as a bare exact figure.
        parsed = parsed with { Reply = EnforceApproximateWording(parsed.Reply, contextResult.Sufficiency.Approximate) };
        // Round-trip the structured references so the client can echo them back on the next
        // turn (see AiConversationState). Not attached to small-talk/guardrail replies -- those
        // deliberately carry no financial state.
        return Ok(parsed with { State = contextResult.OutgoingState });
    }

    private static AiChatOutcome Ok(AiChatResponse response) => new(response, IsProviderError: false);

    // Which optional payload groups this turn is allowed to spend schema budget on. Keep these
    // narrow: every group added to a turn's payload union costs structured-output complexity, and
    // the lite chat model answers 400 INVALID_ARGUMENT rather than degrading when the total is
    // too high. See AiResponseSchemas.Chat.
    private static readonly HashSet<AiIntent> LedgerFilterIntents =
    [
        AiIntent.Navigation, AiIntent.LedgerTransactionList, AiIntent.LedgerMerchantSearch,
        AiIntent.LedgerActivityCount, AiIntent.LedgerSpendingTotal
    ];

    private static bool WantsLedgerFilterControls(AiIntentPlan plan) =>
        plan.Intents.Any(LedgerFilterIntents.Contains);

    private static bool WantsReminderControls(AiIntentPlan plan) =>
        // Read-only recurring questions (cost, list, upcoming bills) do not need reminder-edit
        // fields. Adding that optional group pushes gemini-3.5-flash-lite's chat schema over its
        // structured-output complexity ceiling and makes the whole request fail with HTTP 400.
        plan.Intents.Contains(AiIntent.RecurringEdit);
}
