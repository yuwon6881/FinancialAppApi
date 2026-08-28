using System.Text.Json;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
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
        var accountMentions = await ResolveAccountMentionsAsync(request.AccountMentions, message, cancellationToken);
        // An unanswered request from the previous turn is completed here, before any parsing, so
        // every downstream rule -- intent resolution, the structured draft count, enrichment --
        // sees the whole instruction rather than the fragment that answers it.
        var carriedRequest = IsPendingLedgerAnswer(priorState?.PendingLedgerRequest, message)
            ? priorState!.PendingLedgerRequest
            : null;
        var strippedMessage = StripAccountMentionMarkers(message);
        var effectiveMessage = CombinePendingLedgerRequest(carriedRequest, strippedMessage);
        var resolvedMessage = ApplyInvocationMessage(effectiveMessage, invocationContext);
        var intentPlan = ResolveDeterministically(resolvedMessage, priorState);
        intentPlan = ApplyInvocationPresetPlan(intentPlan, invocationContext);
        if (ShouldUseSemanticPlanner(resolvedMessage, intentPlan, invocationContext))
        {
            var classified = await TryClassifyIntentAsync(resolvedMessage, priorState, cancellationToken);
            if (classified != null &&
                classified.Ambiguities is not { Count: > 0 } &&
                classified.Intents.Any(intent => !intent.Equals("general", StringComparison.OrdinalIgnoreCase)))
            {
                intentPlan = MergeResolutions(resolvedMessage, classified, priorState);
            }
        }
        if (invocationContext?.LoanId is { } loanId &&
            await _loanService.GetLoanAsync(loanId, cancellationToken) == null)
        {
            return Ok(new AiChatResponse("That loan is not available.", [], State: priorState));
        }

        // A staged record has to land in a real account, so a ledger-add turn needs the account
        // list as much as an account question does. Without it the model was told to name an
        // account only from ledgerAccounts and then handed no ledgerAccounts, so it could only ask
        // which account was meant -- and the deterministic placement pass had nothing to match
        // against either. An "@" mention needs it for the same reason.
        if (intentPlan.Intents.Contains(AiIntent.LedgerAdd) || accountMentions.Count > 0)
        {
            intentPlan = intentPlan with
            {
                QueryPlan = intentPlan.QueryPlan with { NeedsLedgerAccounts = true }
            };
        }

        var contextResult = await BuildContextAsync(intentPlan, request.ForceSensitiveMode, cancellationToken);
        var context = contextResult.Context;
        if (context.LedgerAccountSelectionIssue is { } accountIssue)
        {
            return Ok(new AiChatResponse(accountIssue, [], State: contextResult.OutgoingState));
        }
        if (context.SensitiveMode && RequiresSensitiveFinancialReveal(intentPlan))
        {
            return Ok(new AiChatResponse(
                "Sensitive mode is on. Unhide balances before asking for exact amounts or financial analysis.",
                [], State: contextResult.OutgoingState));
        }
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
        var userContent = BuildUserContent(strippedMessage, promptHistory, context, accountMentions, carriedRequest);
        var isLedgerAdd = intentPlan.Intents.Contains(AiIntent.LedgerAdd);
        var isReportReview = intentPlan.Intents.Contains(AiIntent.ReportReview);
        var isInvestmentExplanation = intentPlan.Intents.Any(intent => intent is AiIntent.InvestmentSummary or AiIntent.InvestmentHolding or AiIntent.InvestmentAllocation);
        var isRewardsPlan = intentPlan.Intents.Any(intent => intent is AiIntent.RewardsSummary or AiIntent.SavingsGoalList or
            AiIntent.SavingsGoalPacing or AiIntent.SavingsGoalScenario or AiIntent.SavingsGoalAdd or AiIntent.SavingsGoalEdit);
        var structuredLedgerDraftCount = isLedgerAdd ? CountLedgerDraftListRecords(effectiveMessage) : 0;

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

        var parsed = await ParseAndValidateResponseAsync(text, context, effectiveMessage, intentPlan.Constraints, cancellationToken);
        // Enrichment is a best-effort second pass over an already-valid answer. Its own
        // provider call must never turn a complete reply into a 503, so it degrades to
        // the unenriched drafts instead of propagating.
        try
        {
            parsed = await EnrichLedgerDraftActionsAsync(parsed, effectiveMessage, context, accountMentions, cancellationToken);
        }
        catch (Exception ex) when (ex is AiClientException or JsonException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Ledger draft enrichment failed; returning unenriched drafts.");
        }
        // A staging claim survives only if the turn actually carries the action that stages it.
        parsed = EnforceActionBackedDraftClaims(parsed, context.SensitiveMode);
        // Phase 5: an incomplete aggregate must never surface as a bare exact figure.
        parsed = parsed with { Reply = EnforceApproximateWording(parsed.Reply, contextResult.Sufficiency.Approximate) };
        parsed = parsed with
        {
            Reply = BuildCoverageSentence(contextResult) + parsed.Reply
        };
        // Round-trip the structured references so the client can echo them back on the next
        // turn (see AiConversationState). Not attached to small-talk/guardrail replies -- those
        // deliberately carry no financial state.
        var outgoingState = (contextResult.OutgoingState ?? new AiConversationState(null, null, null, null)) with
        {
            PendingLedgerRequest = ResolvePendingLedgerRequest(effectiveMessage, parsed, isLedgerAdd)
        };
        return Ok(parsed with { State = outgoingState });
    }
}
