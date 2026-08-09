using System.Text.Json;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public sealed partial class AiConversationMemoryService
{
    private async Task<IReadOnlyList<AiActionBatchResponse>> LoadPendingActionBatchesAsync(
        Guid conversationId,
        bool sensitiveMode,
        CancellationToken cancellationToken)
    {
        var query = _context.AiConversationTurns
            .AsNoTracking()
            .Where(turn => turn.ConversationId == conversationId &&
                           turn.Status == "Completed" &&
                           turn.ActionsResolvedAt == null &&
                           turn.ActionsJson != "[]");
        if (sensitiveMode) query = query.Where(turn => turn.SensitiveMode);
        var turns = await query
            .OrderBy(turn => turn.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);
        return turns
            .Select(turn => new AiActionBatchResponse(turn.Id, DeserializeActions(turn.ActionsJson)))
            .Where(batch => batch.Actions.Count > 0)
            .ToList();
    }

    private static AiChatResponse ToReplay(AiConversation conversation, AiConversationTurn turn, bool sensitiveMode)
    {
        if (sensitiveMode && !turn.SensitiveMode)
        {
            return new AiChatResponse(
                "Earlier replies are hidden while sensitive mode is active. Unhide balances to replay this explanation.",
                [],
                State: null,
                ConversationId: conversation.Id,
                ConversationVersion: conversation.Version,
                HistoryRedacted: true);
        }
        var actions = turn.ActionsResolvedAt == null ? DeserializeActions(turn.ActionsJson) : [];
        var batch = actions.Count > 0 ? new AiActionBatchResponse(turn.Id, actions) : null;
        return new AiChatResponse(
            turn.AssistantReply,
            actions,
            turn.CloseChat,
            DeserializeState(conversation.StateJson),
            conversation.Id,
            conversation.Version,
            turn.SensitiveMode == false && sensitiveMode,
            batch);
    }

    private static IReadOnlyList<AiUiAction> DeserializeActions(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<AiUiAction>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
