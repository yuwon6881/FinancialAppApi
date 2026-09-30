using System.Text.Json;
using System.Text.Json.Nodes;
using FinancialAppApi.Services.AI.Agent;
using FinancialAppApi.Services.AI.Tools;

namespace FinancialAppApi.Services;

// One turn on the tool-calling engine. The deterministic guards stay in front (validation, small
// talk, sensitive-mode refusals); after them the model reads the baseline snapshot, chooses its
// own tools, and proposes actions that the server validates. Post-processing (draft enrichment,
// truthful draft claims, approximate wording, pending requests) is shared with the legacy engine.
public partial class AiAssistantService
{
    private const int CarriedTransactionIdLimit = 20;


    private async Task<AiChatOutcome> ChatWithToolsAsync(
        AiChatRequest request,
        IAiAgentProgressSink? sink,
        CancellationToken cancellationToken)
    {
        var agent = _agent;
        var message = (request.Message ?? string.Empty).Trim();
        var priorState = SanitizeConversationState(request.State);
        if (!string.IsNullOrWhiteSpace(message) && IsContextResetRequest(message)) priorState = null;
        if (string.IsNullOrWhiteSpace(message))
            return Ok(new AiChatResponse("Please ask a financial question or tell me what you want to open.", [], State: priorState));
        if (message.Length > MaxMessageLength)
            return Ok(new AiChatResponse("That message is too long. Please shorten it and try again.", [], State: priorState));
        if (!TryNormalizeInvocationContext(request.Context, out var invocation, out var invocationError))
            return Ok(new AiChatResponse(invocationError!, [], State: priorState));
        if (invocation?.SavingsGoalId is { } goalId &&
            !(await _savingsGoalService.GetGoalsAsync(cancellationToken)).Any(goal => goal.Id == goalId))
            return Ok(new AiChatResponse("That savings goal is not available.", [], State: priorState));
        if (invocation?.LoanId is { } loanId && await _loanService.GetLoanAsync(loanId, cancellationToken) == null)
            return Ok(new AiChatResponse("That loan is not available.", [], State: priorState));
        priorState = ApplyInvocationState(priorState, invocation);
        if (TryHandleSmallTalk(message, out var smallTalk))
            return Ok(smallTalk! with { State = smallTalk!.CloseChat ? null : priorState });
        if (!_aiClient.IsConfigured)
            return Ok(new AiChatResponse("AI chat is not configured on the server.", [], State: priorState));

        var accountMentions = await ResolveAccountMentionsAsync(request.AccountMentions, message, cancellationToken);
        var carriedRequest = IsPendingLedgerAnswer(priorState?.PendingLedgerRequest, message) ? priorState!.PendingLedgerRequest : null;
        var strippedMessage = StripAccountMentionMarkers(message);
        var effectiveMessage = CombinePendingLedgerRequest(carriedRequest, strippedMessage);
        var shorthandCount = CountLedgerDraftListRecords(effectiveMessage);

        var toolContext = await agent.Contexts.CreateAsync(request.ForceSensitiveMode, cancellationToken);
        // Only an unmistakable record list is refused up front. Every other change is refused by
        // action validation, so a question that merely mentions "purchase" still gets answered.
        if (toolContext.SensitiveMode && shorthandCount > 0)
        {
            return Ok(new AiChatResponse(
                "Sensitive mode prevents record changes. Unhide balances before editing, deleting, purchasing, confirming, discarding, or toggling a record.",
                [], State: priorState));
        }

        var constraints = ParseConstraints(effectiveMessage);
        var carriedIds = priorState?.LastMatchedTransactionIds?.Take(CarriedTransactionIdLimit).ToList() ?? [];
        var categories = (await _categoryService.GetCategoriesAsync())
            .Where(category => !TransactionCategoryService.IsReservedName(category.Name))
            .Select(category => category.Name)
            .OrderBy(name => name)
            .ToList();
        var proposer = new AgentActionProposer(this, toolContext, effectiveMessage, constraints, carriedIds, categories, shorthandCount);
        var snapshot = await agent.Snapshot.BuildAsync(toolContext, cancellationToken);

        AiAgentTurnResult turn;
        try
        {
            turn = await agent.Engine.RunAsync(
                new AiAgentTurnRequest(
                    strippedMessage,
                    BuildAgentPriorInput(request.History, snapshot, categories, accountMentions, carriedRequest, invocation, carriedIds),
                    toolContext,
                    proposer,
                    SeededCallsFor(invocation, priorState),
                    ForcedFirstTool: shorthandCount > 0 ? AiAgentPrompt.ProposeActionsTool : null,
                    Sink: sink == null ? null : new SensitiveAwareSink(sink, toolContext)),
                cancellationToken);
        }
        catch (AiClientException ex)
        {
            return new AiChatOutcome(new AiChatResponse(ex.Message, []), IsProviderError: true);
        }

        try
        {
            await agent.Usage.RecordAsync(turn.Usage, turn.Rounds, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Metering is bookkeeping; a failed write must never cost the user their answer.
            _logger.LogWarning(ex, "Ask AI usage could not be recorded.");
        }

        var actions = turn.Actions.ToList();
        var response = new AiChatResponse(
            turn.Reply,
            actions,
            CloseChat: actions.Count > 0 &&
                actions.All(action => !action.Type.StartsWith("openEdit", StringComparison.OrdinalIgnoreCase)) &&
                !LooksLikeFollowUp(turn.Reply));
        if (proposer.ValidationContext is { } validationContext)
        {
            try
            {
                response = await EnrichLedgerDraftActionsAsync(response, effectiveMessage, validationContext, accountMentions, cancellationToken);
            }
            catch (Exception ex) when (ex is AiClientException or JsonException or HttpRequestException)
            {
                _logger.LogWarning(ex, "Ledger draft enrichment failed; returning unenriched drafts.");
            }
        }
        response = EnforceActionBackedDraftClaims(response, toolContext.SensitiveMode, ClassifyDraftRequest(effectiveMessage, shorthandCount, proposer.AttemptedDraftTypes));
        response = response with { Reply = EnforceApproximateWording(response.Reply, turn.AnyApproximate) };

        var isLedgerAdd = shorthandCount > 0 || response.Actions.Any(action => action.Type == "openAddLedgerDraft");
        var outgoingState = new AiConversationState
        {
            LastMatchedTransactionIds = toolContext.Evidence.IdsOf(AiEvidenceLedger.Transaction)
                .Take(CarriedTransactionIdLimit).ToList() is { Count: > 0 } ids ? ids : null,
            LastLoanId = priorState?.LastLoanId,
            LastInvestmentRange = priorState?.LastInvestmentRange,
            LastSavingsGoalId = priorState?.LastSavingsGoalId,
            LastReportCycleKey = priorState?.LastReportCycleKey,
            PendingLedgerRequest = ResolvePendingLedgerRequest(effectiveMessage, response, isLedgerAdd)
        };
        return new AiChatOutcome(response with { State = outgoingState }, IsProviderError: false, ToolTrace: turn.Trace);
    }

    // Read from the user's own words and the model's own attempts, never from the reply: the reply
    // is what is being checked.
    private static DraftRequest ClassifyDraftRequest(string message, int shorthandCount, IReadOnlySet<string> attemptedDraftTypes)
    {
        if (shorthandCount > 0 || attemptedDraftTypes.Contains("openAddLedgerDraft") ||
            HasExplicitMutationCommand(message, "openAddLedgerDraft"))
            return DraftRequest.LedgerAdd;
        return attemptedDraftTypes.Count > 0 || HasExplicitMutationCommand(message, "openEditLedgerDraft")
            ? DraftRequest.OtherChange
            : DraftRequest.None;
    }

    private static List<JsonObject> BuildAgentPriorInput(
        IReadOnlyList<AiChatMessage>? history,
        JsonObject snapshot,
        IReadOnlyList<string> categories,
        IReadOnlyList<AiResolvedAccountMention> accountMentions,
        string? carriedRequest,
        AiInvocationContext? invocation,
        IReadOnlyList<string> carriedIds)
    {
        var context = new JsonObject
        {
            ["snapshot"] = snapshot,
            ["categories"] = new JsonArray(categories.Select(name => (JsonNode)name).ToArray())
        };
        if (accountMentions.Count > 0)
        {
            // Accounts the user picked with "@", in message order: the first is a transfer's source.
            context["mentionedAccounts"] = new JsonArray(accountMentions
                .Select(mention => (JsonNode)new JsonObject { ["accountId"] = mention.AccountId, ["name"] = mention.Name, ["bucket"] = mention.Bucket })
                .ToArray());
        }
        if (carriedRequest != null) context["pendingRequest"] = carriedRequest;
        if (invocation != null)
        {
            context["openedFrom"] = new JsonObject
            {
                ["screen"] = invocation.Surface,
                ["preset"] = invocation.Preset,
                ["cycleKey"] = invocation.CycleKey,
                ["investmentRange"] = invocation.InvestmentRange,
                ["loanId"] = invocation.LoanId
            };
        }
        if (carriedIds.Count > 0) context["previousTurnTransactionIds"] = new JsonArray(carriedIds.Select(id => (JsonNode)id).ToArray());

        var input = new List<JsonObject>();
        // Earlier turns are real dialogue for continuity. Their figures are stale by definition;
        // current facts come from the snapshot and tools.
        foreach (var turn in SanitizeHistory(history))
            input.Add(AiInputItems.Message(turn.Role == "assistant" ? "assistant" : "user", turn.Content));
        input.Add(AiInputItems.Message("developer",
            "Current app context (authoritative now; earlier replies may be stale). pendingRequest, when present, is the " +
            "user's earlier request that this message answers: treat both as one instruction.\n" +
            context.ToJsonString()));
        return input;
    }

    // Once sensitive mode switches on mid-turn, text already on screen is withdrawn and no more is
    // streamed; the final reply is then replaced by the conversation layer's sensitive-mode notice.
    private sealed class SensitiveAwareSink(IAiAgentProgressSink inner, AiToolContext context) : IAiAgentProgressSink
    {
        private readonly bool _startedHidden = context.SensitiveMode;
        private bool _withdrawn;

        public ValueTask OnStatusAsync(string label, CancellationToken cancellationToken) =>
            inner.OnStatusAsync(label, cancellationToken);

        public async ValueTask OnTextDeltaAsync(string delta, CancellationToken cancellationToken)
        {
            if (!_startedHidden && context.SensitiveMode)
            {
                if (_withdrawn) return;
                _withdrawn = true;
                await inner.OnTextResetAsync(cancellationToken);
                return;
            }
            await inner.OnTextDeltaAsync(delta, cancellationToken);
        }

        public ValueTask OnTextResetAsync(CancellationToken cancellationToken) =>
            inner.OnTextResetAsync(cancellationToken);
    }

    // A screen's "Explain this" button makes its first lookup obvious; running it up front saves
    // a round trip without taking the choice of any further tools away from the model.
    private static IReadOnlyList<AiSeededToolCall>? SeededCallsFor(AiInvocationContext? invocation, AiConversationState? state) =>
        invocation?.Preset switch
        {
            "report-review" => [new("analyze_transactions", JsonSerializer.Serialize(new { kind = "review", cycleKeys = new[] { invocation.CycleKey ?? "current" } }))],
            "investment-explain" => [new("get_investments", JsonSerializer.Serialize(new { range = invocation.InvestmentRange ?? state?.LastInvestmentRange ?? "1y" }))],
            "rewards-plan" => [new("get_goals_and_rewards", "{}")],
            "loan-explain" => [new("get_loans", JsonSerializer.Serialize(new { loanId = invocation.LoanId }))],
            _ => null
        };
}
