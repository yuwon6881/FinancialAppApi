using FinancialAppApi.Database;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

// Structured conversation frame persisted by the server-owned active conversation. Legacy
// clients may still echo it on AiChatRequest, so it remains untrusted at the boundary. It holds
// only references and validated user-supplied query parameters (never observed balances or full
// records); IDs are always re-derived against the database.
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
    decimal? LastTargetAmount = null,
    string? LastRewardsTopic = null,
    int? LastSavingsGoalId = null,
    string? LastInvestmentTopic = null,
    string? LastInvestmentRange = null,
    Guid? LastInvestmentInstrumentId = null,
    string? LastReportCycleKey = null);

public sealed record AiInvocationContext(
    string Surface,
    string? Preset = null,
    string? CycleKey = null,
    string? InvestmentRange = null,
    int? SavingsGoalId = null,
    bool HasPendingLocalChanges = false);

public sealed record AiChatMessage(string Role, string Content);
public sealed record AiChatRequest(
    string Message,
    IReadOnlyList<AiChatMessage>? History,
    AiConversationState? State = null,
    Guid? ConversationId = null,
    int? ConversationVersion = null,
    string? ClientTurnId = null,
    AiInvocationContext? Context = null);
public sealed record AiChatResponse(
    string Reply,
    IReadOnlyList<AiUiAction> Actions,
    bool CloseChat = false,
    AiConversationState? State = null,
    Guid? ConversationId = null,
    int? ConversationVersion = null,
    bool HistoryRedacted = false);
public sealed record AiUiAction(string Type, Dictionary<string, object?> Payload);
public sealed record AiConversationResponse(
    Guid? ConversationId,
    int ConversationVersion,
    IReadOnlyList<AiChatMessage> Messages,
    AiConversationState? State,
    bool HistoryRedacted = false);

// AiChatResponse alone is the wire shape returned to the client either way (a friendly
// message is a valid chat reply whether or not the AI provider itself succeeded) -- but the
// controller still needs to know whether to report 200 or 503, the same way every other AI
// endpoint's controller switches on a Status field instead of guessing from the payload.
public sealed record AiChatOutcome(AiChatResponse Response, bool IsProviderError, bool IsConflict = false);

public partial class AiAssistantService
{
    private static readonly string[] LedgerCategories = ["Essentials", "Growth", "Stability", "Rewards", "Income"];
    private static readonly HashSet<string> AllowedActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openLedger",
        "openDashboard",
        "openRecurring",
        "openWishlist",
        "openReports",
        "openInvestments",
        "openAddLedgerDraft",
        "openAddRecurringDraft",
        "openAddWishlistDraft",
        "openEditLedgerDraft",
        "openEditRecurringDraft",
        "openEditWishlistDraft",
        "openAddSavingsGoalDraft",
        "openEditSavingsGoalDraft",
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
    private readonly AiConversationMemoryService _conversationMemory;
    private readonly SavingsGoals.SavingsGoalService _savingsGoalService;
    private readonly Investments.InvestmentPortfolioService? _investmentPortfolioService;

    public AiAssistantService(
        AiClient aiClient,
        AppDbContext context,
        TransactionCategoryService categoryService,
        CategorySuggestionService? categorySuggestionService = null,
        RecurringOccurrenceService? recurringOccurrenceService = null,
        FinancialClock? financialClock = null,
        ILogger<AiAssistantService>? logger = null,
        AiConversationMemoryService? conversationMemory = null,
        SavingsGoals.SavingsGoalService? savingsGoalService = null,
        Investments.InvestmentPortfolioService? investmentPortfolioService = null)
    {
        _logger = logger ?? NullLogger<AiAssistantService>.Instance;
        _aiClient = aiClient;
        _context = context;
        _categoryService = categoryService;
        _categorySuggestionService = categorySuggestionService;
        _recurringOccurrenceService = recurringOccurrenceService ??
            new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        _financialClock = financialClock ?? FinancialClock.Utc;
        _conversationMemory = conversationMemory ?? new AiConversationMemoryService(context);
        _savingsGoalService = savingsGoalService ?? new SavingsGoals.SavingsGoalService(
            context,
            new CycleBalanceService(context),
            _financialClock);
        _investmentPortfolioService = investmentPortfolioService;
    }

    public async Task<AiChatOutcome> ChatAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        // A clientTurnId opts the request into server-owned conversation state. Existing clients
        // that do not send one retain the legacy history/state behavior until they are upgraded.
        if (string.IsNullOrWhiteSpace(request.ClientTurnId) && request.ConversationId == null)
        {
            return await ChatCoreAsync(request, cancellationToken);
        }

        var prepared = await _conversationMemory.PrepareAsync(request, cancellationToken);
        if (prepared.Replay != null)
        {
            return Ok(prepared.Replay);
        }
        if (prepared.Conflict || prepared.Conversation == null)
        {
            return new AiChatOutcome(
                new AiChatResponse(
                    "This conversation changed on another device. Reload it and retry your message.",
                    [],
                    State: prepared.State,
                    ConversationId: prepared.Conversation?.Id,
                    ConversationVersion: prepared.Conversation?.Version),
                IsProviderError: false,
                IsConflict: true);
        }

        var serverRequest = request with
        {
            History = prepared.History,
            State = prepared.State
        };
        var outcome = await ChatCoreAsync(serverRequest, cancellationToken);
        if (outcome.IsProviderError)
        {
            return outcome with
            {
                Response = outcome.Response with
                {
                    ConversationId = prepared.Conversation.Id,
                    ConversationVersion = prepared.Conversation.Version
                }
            };
        }

        var completed = await _conversationMemory.CompleteAsync(
            prepared,
            request.Message,
            outcome.Response,
            cancellationToken);
        if (completed == null)
        {
            return new AiChatOutcome(
                new AiChatResponse(
                    "This conversation changed on another device. Reload it and retry your message.",
                    [],
                    State: prepared.State,
                    ConversationId: prepared.Conversation.Id,
                    ConversationVersion: prepared.Conversation.Version),
                IsProviderError: false,
                IsConflict: true);
        }

        return outcome with { Response = completed };
    }

    private async Task<AiChatOutcome> ChatCoreAsync(AiChatRequest request, CancellationToken cancellationToken)
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
        if (!TryNormalizeInvocationContext(request.Context, out var invocationContext, out var invocationError))
        {
            return Ok(new AiChatResponse(invocationError!, [], State: priorState));
        }
        if (invocationContext?.SavingsGoalId is { } savingsGoalId)
        {
            var goalExists = (await _savingsGoalService.GetGoalsAsync(cancellationToken))
                .Any(goal => goal.Id == savingsGoalId);
            if (!goalExists)
            {
                return Ok(new AiChatResponse("That savings goal is not available.", [], State: priorState));
            }
        }
        priorState = ApplyInvocationState(priorState, invocationContext);
        if (TryHandleSmallTalk(message, out var smallTalkResponse))
        {
            return Ok(smallTalkResponse! with { State = smallTalkResponse!.CloseChat ? null : priorState });
        }

        if (!_aiClient.IsConfigured)
        {
            return Ok(new AiChatResponse("AI chat is not configured on the server.", [], State: priorState));
        }

        var history = SanitizeHistory(request.History);
        var resolvedMessage = ApplyInvocationMessage(message, invocationContext);
        var intentPlan = ResolveDeterministically(resolvedMessage, priorState);
        if (intentPlan.Confidence < 0.72 || intentPlan.Intents.Contains(AiIntent.General))
        {
            var classified = await TryClassifyIntentAsync(resolvedMessage, priorState, cancellationToken);
            if (classified != null && classified.Confidence >= intentPlan.Confidence)
            {
                intentPlan = MergeResolutions(resolvedMessage, classified, priorState);
            }
        }

        var contextResult = await BuildContextAsync(intentPlan, cancellationToken);
        var context = contextResult.Context;
        if (context.SensitiveMode && RequiresSensitiveFinancialReveal(intentPlan))
        {
            return Ok(new AiChatResponse(
                "Unhide balances before asking for a Rewards, investment, or report explanation.",
                [], State: contextResult.OutgoingState));
        }
        var pendingSyncWarning = invocationContext?.HasPendingLocalChanges == true
            ? " This uses saved server data and does not include changes still syncing."
            : string.Empty;
        if (context.SensitiveMode &&
            (LooksLikeProtectedMutationCommand(message) ||
             intentPlan.Intents.Contains(AiIntent.LedgerAdd)))
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

        // Server-owned memory supplies a bounded mix of the latest dialogue and relevant older
        // turns. Structured state still reconstructs exact scopes and IDs; live app context below
        // remains authoritative for every financial value.
        var promptHistory = history;
        var systemInstruction = SystemInstruction;
        var userContent = BuildUserContent(message, promptHistory, context);
        var isLedgerAdd = intentPlan.Intents.Contains(AiIntent.LedgerAdd);
        var isReportReview = intentPlan.Intents.Contains(AiIntent.ReportReview);
        var isInvestmentExplanation = intentPlan.Intents.Any(intent => intent is AiIntent.InvestmentSummary or AiIntent.InvestmentHolding or AiIntent.InvestmentAllocation);
        var isRewardsPlan = intentPlan.Intents.Any(intent => intent is AiIntent.RewardsSummary or AiIntent.SavingsGoalList or
            AiIntent.SavingsGoalPacing or AiIntent.SavingsGoalScenario or AiIntent.SavingsGoalAdd or AiIntent.SavingsGoalEdit);
        var structuredLedgerDraftCount = isLedgerAdd ? CountLedgerDraftListRecords(message) : 0;

        string text;
        try
        {
            text = await _aiClient.GenerateTextAsync(
                [AiPart.FromText(userContent)],
                new AiGenerationOptions(
                    Feature: "chat",
                    Temperature: 0.15,
                    // Reasoning tokens are drawn from this same budget, so leave enough headroom
                    // for the visible structured reply.
                    MaxOutputTokens: intentPlan.Intents.Contains(AiIntent.LedgerAdd)
                        ? 3200
                        : intentPlan.QueryPlan.NeedsCycleComparison ? 1600 : 1200,
                    SystemInstruction: systemInstruction,
                    OutputJsonSchema: isLedgerAdd
                        ? AiResponseSchemas.LedgerDraftChat(context.Categories, structuredLedgerDraftCount)
                        : isReportReview
                            ? AiResponseSchemas.ReportReviewChat()
                            : isInvestmentExplanation
                                ? AiResponseSchemas.InvestmentExplainChat()
                                : isRewardsPlan
                                    ? AiResponseSchemas.RewardsPlanChat()
                        : AiResponseSchemas.Chat(
                            context.Categories,
                            includeLedgerFilters: WantsLedgerFilterControls(intentPlan),
                            includeReminderControls: WantsReminderControls(intentPlan)),
                    ThinkingLevel: intentPlan.QueryPlan.NeedsCycleComparison ? "medium" : "low",
                    ModelConfigurationKey: "OpenAiModels:Chat"),
                cancellationToken);
        }
        catch (AiClientException ex)
        {
            // Matches every other AI endpoint: a Status-style signal the controller switches
            // on to pick 200 vs 503, not a thrown exception crossing the service boundary --
            // the friendly reply text is still the same AiChatResponse shape either way.
            return new AiChatOutcome(new AiChatResponse(ex.Message, []), IsProviderError: true);
        }

        var parsed = await ParseAndValidateResponseAsync(text, context, message, intentPlan.Constraints, cancellationToken);
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
        parsed = parsed with
        {
            Reply = BuildCoverageSentence(intentPlan, contextResult, pendingSyncWarning) + parsed.Reply
        };
        // Round-trip the structured references so the client can echo them back on the next
        // turn (see AiConversationState). Not attached to small-talk/guardrail replies -- those
        // deliberately carry no financial state.
        return Ok(parsed with { State = contextResult.OutgoingState });
    }

    private static AiChatOutcome Ok(AiChatResponse response) => new(response, IsProviderError: false);

    private static bool RequiresSensitiveFinancialReveal(AiIntentPlan intentPlan) =>
        intentPlan.Intents.Any(intent => intent is AiIntent.RewardsSummary or AiIntent.SavingsGoalList or
            AiIntent.SavingsGoalPacing or AiIntent.SavingsGoalScenario or AiIntent.SavingsGoalAdd or
            AiIntent.SavingsGoalEdit or AiIntent.InvestmentSummary or AiIntent.InvestmentHolding or
            AiIntent.InvestmentAllocation or AiIntent.ReportReview);

    private static string BuildCoverageSentence(
        AiIntentPlan intentPlan,
        AiContextBuildResult contextResult,
        string pendingSyncWarning)
    {
        var query = intentPlan.QueryPlan.QueryText;
        var scope = Regex.IsMatch(query, @"\ball[- ]?time\b", RegexOptions.IgnoreCase)
            ? "Coverage: this analysis is limited to the last 24 cycles, so it is not an exact all-time review."
            : contextResult.Sufficiency.Approximate
                ? "Coverage: some saved rows were sampled, so the figures below are approximate."
                : "Coverage: figures below use the selected cycle and saved server data.";
        return scope + pendingSyncWarning + " ";
    }

    private static readonly HashSet<string> InvocationSurfaces =
        new(StringComparer.OrdinalIgnoreCase) { "dashboard", "reports", "recurring", "ledger", "wishlist", "drafts", "settings", "investments", "documents" };

    private static readonly HashSet<string> InvocationPresets =
        new(StringComparer.OrdinalIgnoreCase) { "report-review", "investment-explain", "rewards-plan" };

    private static readonly HashSet<string> InvocationRanges =
        new(StringComparer.OrdinalIgnoreCase) { "1m", "3m", "6m", "1y", "3y", "5y", "all" };

    private static bool TryNormalizeInvocationContext(
        AiInvocationContext? context,
        out AiInvocationContext? normalized,
        out string? error)
    {
        normalized = null;
        error = null;
        if (context == null) return true;
        var surface = context.Surface?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(surface) || !InvocationSurfaces.Contains(surface))
        {
            error = "That screen is not a valid Ask AI context.";
            return false;
        }
        var preset = string.IsNullOrWhiteSpace(context.Preset) ? null : context.Preset.Trim().ToLowerInvariant();
        if (preset != null && !InvocationPresets.Contains(preset))
        {
            error = "That Ask AI explanation type is not available.";
            return false;
        }
        var range = string.IsNullOrWhiteSpace(context.InvestmentRange) ? null : context.InvestmentRange.Trim().ToLowerInvariant();
        if (range != null && !InvocationRanges.Contains(range))
        {
            error = "That investment time range is not available.";
            return false;
        }
        var cycleKey = string.IsNullOrWhiteSpace(context.CycleKey) ? null : context.CycleKey.Trim();
        if (cycleKey != null && !Regex.IsMatch(cycleKey, @"^(?:19|20)\d{2}-(?:0[1-9]|1[0-2])$"))
        {
            error = "That cycle reference is not valid.";
            return false;
        }
        if (context.SavingsGoalId is <= 0)
        {
            error = "That savings goal reference is not valid.";
            return false;
        }
        normalized = new AiInvocationContext(surface, preset, cycleKey, range, context.SavingsGoalId, context.HasPendingLocalChanges);
        return true;
    }

    private static AiConversationState? ApplyInvocationState(
        AiConversationState? state,
        AiInvocationContext? context)
    {
        if (context == null) return state;
        return (state ?? new AiConversationState(null, null, null, null)) with
        {
            LastInvestmentRange = context.InvestmentRange ?? state?.LastInvestmentRange,
            LastSavingsGoalId = context.SavingsGoalId ?? state?.LastSavingsGoalId,
            LastReportCycleKey = context.CycleKey ?? state?.LastReportCycleKey,
            LastInvestmentTopic = context.Preset == "investment-explain" ? "portfolio" : state?.LastInvestmentTopic,
            LastRewardsTopic = context.Preset == "rewards-plan" ? "plan" : state?.LastRewardsTopic
        };
    }

    private static string ApplyInvocationMessage(string message, AiInvocationContext? context)
    {
        if (context == null) return message;
        var suffix = new List<string>();
        if (context.CycleKey != null) suffix.Add($"for cycle {context.CycleKey}");
        if (context.InvestmentRange != null) suffix.Add($"using investment range {context.InvestmentRange}");
        if (context.Preset == "report-review") suffix.Add("report review");
        if (context.Preset == "investment-explain") suffix.Add("portfolio explanation");
        if (context.Preset == "rewards-plan") suffix.Add("Rewards plan");
        return suffix.Count == 0 ? message : $"{message} ({string.Join(", ", suffix)})";
    }

    // Keep optional payload groups intent-specific so unrelated controls do not distract the model
    // or inflate the structured output contract.
    private static readonly HashSet<AiIntent> LedgerFilterIntents =
    [
        AiIntent.Navigation, AiIntent.LedgerTransactionList, AiIntent.LedgerMerchantSearch,
        AiIntent.LedgerActivityCount, AiIntent.LedgerSpendingTotal
    ];

    private static bool WantsLedgerFilterControls(AiIntentPlan plan) =>
        plan.Intents.Any(LedgerFilterIntents.Contains);

    private static bool WantsReminderControls(AiIntentPlan plan) =>
        // Read-only recurring questions (cost, list, upcoming bills) do not need reminder-edit
        // fields, so do not carry that optional group on read-only requests.
        plan.Intents.Contains(AiIntent.RecurringEdit);
}
