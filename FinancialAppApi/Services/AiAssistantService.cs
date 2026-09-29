using FinancialAppApi.Database;
using FinancialAppApi.Services.AI.Agent;

namespace FinancialAppApi.Services;

// What one Ask AI turn hands the next, persisted by the server-owned conversation. Clients may
// still echo it, so it is untrusted at the boundary: ids are only hints the next turn re-reads
// from the database, and no observed amount or record is ever stored here.
public sealed record AiConversationState(
    // Transactions the previous turn surfaced, so "delete that one" can target them.
    IReadOnlyList<string>? LastMatchedTransactionIds = null,
    // References a screen's "Explain this" button opened the chat with.
    int? LastSavingsGoalId = null,
    string? LastInvestmentRange = null,
    string? LastReportCycleKey = null,
    string? LastLoanId = null,
    // A record request the assistant could not stage because it asked one clarifying question.
    // The answer ("yes, cimb to ryt") carries no amount or verb, so on its own it is not a
    // request; carrying the unanswered one forward is what makes the answer completable.
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
// ToolTrace carries the turn's lookups to conversation memory; it never reaches the client.
public sealed record AiChatOutcome(
    AiChatResponse Response,
    bool IsProviderError,
    bool IsConflict = false,
    IReadOnlyList<AI.Agent.AiToolTrace>? ToolTrace = null);

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

    private readonly AiClient _aiClient;
    private readonly AppDbContext _context;
    private readonly TransactionCategoryService _categoryService;
    private readonly RecurringOccurrenceLedgerService _recurringOccurrenceLedger;
    private readonly FinancialClock _financialClock;
    private readonly ILogger<AiAssistantService> _logger;
    private readonly AiConversationMemoryService _conversationMemory;
    private readonly SavingsGoals.SavingsGoalService _savingsGoalService;
    private readonly Loans.LoanService _loanService;
    private readonly Accounts.LedgerAccountService _ledgerAccountService;
    private readonly AiAgentServices _agent;

    public AiAssistantService(
        AiClient aiClient,
        AppDbContext context,
        TransactionCategoryService categoryService,
        AiAgentServices agent,
        AiConversationMemoryService conversationMemory,
        RecurringOccurrenceLedgerService recurringOccurrenceLedger,
        SavingsGoals.SavingsGoalService savingsGoalService,
        Loans.LoanService loanService,
        Accounts.LedgerAccountService ledgerAccountService,
        FinancialClock financialClock,
        ILogger<AiAssistantService> logger)
    {
        _aiClient = aiClient;
        _context = context;
        _categoryService = categoryService;
        _agent = agent;
        _conversationMemory = conversationMemory;
        _recurringOccurrenceLedger = recurringOccurrenceLedger;
        _savingsGoalService = savingsGoalService;
        _loanService = loanService;
        _ledgerAccountService = ledgerAccountService;
        _financialClock = financialClock;
        _logger = logger;
    }

    public Task<AiChatOutcome> ChatAsync(AiChatRequest request, CancellationToken cancellationToken = default) =>
        ChatAsync(request, sink: null, cancellationToken);

    // sink, when given, receives live progress: tool status and provisional reply text.
    public async Task<AiChatOutcome> ChatAsync(
        AiChatRequest request,
        IAiAgentProgressSink? sink,
        CancellationToken cancellationToken = default)
    {
        // A clientTurnId opts the request into server-owned conversation memory. A request without
        // one carries its own history and state, both sanitized as untrusted input.
        if (string.IsNullOrWhiteSpace(request.ClientTurnId) && request.ConversationId == null)
        {
            return await ChatWithToolsAsync(request, sink, cancellationToken);
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
            outcome = await ChatWithToolsAsync(serverRequest, sink, cancellationToken);
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
            cancellationToken,
            outcome.ToolTrace);
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
}
