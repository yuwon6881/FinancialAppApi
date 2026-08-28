using FinancialAppApi.Database;
using Microsoft.Extensions.Logging.Abstractions;
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
    string? LastReportCycleKey = null,
    string? LastLoanId = null,
    string? LastLedgerAccountId = null,
    // A record request the assistant could not stage because it asked one clarifying question.
    // The answer to that question ("yes, cimb to ryt") carries no amount, no description and no
    // verb, so on its own it resolves to no intent at all and the next turn restarts from
    // nothing -- which is how a staged request evaporated into "send it as a description and an
    // amount on one line". History cannot rescue it either: prior turns are quoted as untrusted
    // dialogue the model is explicitly told never to act on. Carrying the unanswered request
    // forward as structured state is what makes the answer completable.
    string? PendingLedgerRequest = null);

public sealed record AiInvocationContext(
    string Surface,
    string? Preset = null,
    string? CycleKey = null,
    string? InvestmentRange = null,
    int? SavingsGoalId = null,
    string? LoanId = null);

public sealed record AiChatMessage(string Role, string Content);

// One "@account" the user picked in the composer. The client resolves the token against the
// accounts it already holds, but the id is re-validated against the tenant's own accounts before
// it can reach a draft -- a client-supplied account id is untrusted like every other echoed
// reference. Token is the literal text typed after "@", kept so the server can still resolve a
// mention by name when an older client sends no id.
public sealed record AiAccountMention(string Token, string? AccountId = null);

public sealed record AiChatRequest(
    string Message,
    IReadOnlyList<AiChatMessage>? History,
    AiConversationState? State = null,
    Guid? ConversationId = null,
    int? ConversationVersion = null,
    string? ClientTurnId = null,
    AiInvocationContext? Context = null,
    bool ForceSensitiveMode = false,
    int ClientContractVersion = 1,
    IReadOnlyList<AiAccountMention>? AccountMentions = null);
public sealed record AiChatResponse(
    string Reply,
    IReadOnlyList<AiUiAction> Actions,
    bool CloseChat = false,
    AiConversationState? State = null,
    Guid? ConversationId = null,
    int? ConversationVersion = null,
    bool HistoryRedacted = false,
    AiActionBatchResponse? ActionBatch = null);
public sealed record AiUiAction(string Type, Dictionary<string, object?> Payload, Guid? ActionId = null);
public sealed record AiActionBatchResponse(
    Guid BatchId,
    IReadOnlyList<AiUiAction> Actions,
    string Status = "PendingReview");
public sealed record AiConversationResponse(
    Guid? ConversationId,
    int ConversationVersion,
    IReadOnlyList<AiChatMessage> Messages,
    AiConversationState? State,
    bool HistoryRedacted = false,
    IReadOnlyList<AiActionBatchResponse>? PendingActionBatches = null);

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
    private readonly RecurringOccurrenceService _recurringOccurrenceService;
    private readonly RecurringOccurrenceLedgerService _recurringOccurrenceLedger;
    private readonly FinancialClock _financialClock;
    private readonly ILogger<AiAssistantService> _logger;
    private readonly AiConversationMemoryService _conversationMemory;
    private readonly SavingsGoals.SavingsGoalService _savingsGoalService;
    private readonly Investments.InvestmentPortfolioService? _investmentPortfolioService;
    private readonly Loans.LoanService _loanService;
    private readonly Accounts.LedgerAccountService _ledgerAccountService;

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
        Investments.InvestmentPortfolioService? investmentPortfolioService = null,
        RecurringOccurrenceLedgerService? recurringOccurrenceLedger = null,
        Loans.LoanService? loanService = null,
        Accounts.LedgerAccountService? ledgerAccountService = null)
    {
        _logger = logger ?? NullLogger<AiAssistantService>.Instance;
        _aiClient = aiClient;
        _context = context;
        _categoryService = categoryService;
        _ = categorySuggestionService;
        _recurringOccurrenceService = recurringOccurrenceService ??
            new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        _financialClock = financialClock ?? FinancialClock.Utc;
        _recurringOccurrenceLedger = recurringOccurrenceLedger ??
            new RecurringOccurrenceLedgerService(context, _recurringOccurrenceService, _financialClock);
        _conversationMemory = conversationMemory ?? new AiConversationMemoryService(context);
        _savingsGoalService = savingsGoalService ?? new SavingsGoals.SavingsGoalService(
            context,
            new CycleBalanceService(context),
            _financialClock,
            _recurringOccurrenceService);
        _investmentPortfolioService = investmentPortfolioService;
        _loanService = loanService ?? new Loans.LoanService(context);
        _ledgerAccountService = ledgerAccountService ?? new Accounts.LedgerAccountService(
            context,
            new Accounts.LedgerAccountBalanceService(
                context,
                new CycleBalanceService(context),
                _financialClock),
            new CycleBalanceService(context),
            _financialClock);
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
        AiChatOutcome outcome;
        try
        {
            outcome = await ChatCoreAsync(serverRequest, cancellationToken);
        }
        catch
        {
            await _conversationMemory.FailAsync(prepared, CancellationToken.None);
            throw;
        }
        if (outcome.IsProviderError)
        {
            await _conversationMemory.FailAsync(prepared, cancellationToken);
            return outcome with
            {
                Response = outcome.Response with
                {
                    ConversationId = prepared.Conversation.Id,
                    ConversationVersion = prepared.Conversation.Version
                }
            };
        }

        var effectiveSensitiveMode = await _conversationMemory.ResolveEffectiveSensitiveModeAsync(
            request.ForceSensitiveMode,
            cancellationToken);
        if (effectiveSensitiveMode && !prepared.SensitiveMode)
        {
            prepared = prepared with { SensitiveMode = true };
            outcome = outcome with
            {
                Response = new AiChatResponse(
                    "Sensitive mode was enabled while I was answering, so I hid that response. Ask again after unhiding balances if you still need it.",
                    [],
                    State: outcome.Response.State)
            };
        }

        var completed = await _conversationMemory.CompleteAsync(
            prepared,
            request.Message,
            outcome.Response,
            cancellationToken);
        if (completed == null)
        {
            await _conversationMemory.FailAsync(prepared, cancellationToken);
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


    private static AiChatOutcome Ok(AiChatResponse response) => new(response, IsProviderError: false);

    private static readonly Regex AmbiguousRoutingSignal = new(
        @"\b(goals?|bills?|afford|room|performance|nest egg|other one|biggest one|first finding|tell me more)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool ShouldUseSemanticPlanner(
        string message,
        AiIntentPlan deterministic,
        AiInvocationContext? invocation)
    {
        if (CountLedgerDraftListRecords(message) > 0) return false;
        if (invocation?.Preset != null) return false;
        if (deterministic.Intents.Any(intent => intent is AiIntent.LedgerAdd or AiIntent.LedgerEdit or
                AiIntent.RecurringAdd or AiIntent.RecurringEdit or AiIntent.WishlistAdd or AiIntent.WishlistEdit or
                AiIntent.SavingsGoalAdd or AiIntent.SavingsGoalEdit) ||
            LooksLikeProtectedMutationCommand(message)) return false;
        if (deterministic.Intents.Contains(AiIntent.General)) return true;
        if (AmbiguousRoutingSignal.IsMatch(message)) return true;
        if (deterministic.Intents.Any(intent => intent is AiIntent.RewardsSummary or AiIntent.SavingsGoalList or
                AiIntent.SavingsGoalPacing or AiIntent.SavingsGoalScenario or AiIntent.SavingsGoalAdd or
                AiIntent.SavingsGoalEdit or AiIntent.InvestmentSummary or AiIntent.InvestmentHolding or
                AiIntent.InvestmentAllocation or AiIntent.ReportReview or AiIntent.LedgerAccount)) return true;
        var wordCount = message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return wordCount <= 5 && !Regex.IsMatch(
            message,
            @"^\s*(?:open|show|go to|navigate|take me)\b",
            RegexOptions.IgnoreCase);
    }

    private static bool RequiresSensitiveFinancialReveal(AiIntentPlan intentPlan) =>
        intentPlan.Intents.Any(intent =>
            AiCapabilities.Any(capability => capability.Intent == intent && capability.RequiresSensitiveReveal));

    private static string BuildCoverageSentence(AiContextBuildResult contextResult)
    {
        var scope = contextResult.Sufficiency.Approximate
                ? "Coverage: some saved rows were sampled, so the figures below are approximate."
                : string.Empty;

        return string.IsNullOrEmpty(scope) ? string.Empty : scope + " ";
    }

}
