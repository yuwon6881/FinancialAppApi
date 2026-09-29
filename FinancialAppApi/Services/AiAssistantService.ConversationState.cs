using System.Globalization;
using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private const int MaxHistoryTurns = 12;
    private const int MaxStateMatchedIds = 50;
    private const int MaxStateReferenceLength = 100;
    private static readonly HashSet<string> StateInvestmentRanges =
        new(StringComparer.OrdinalIgnoreCase) { "1m", "3m", "6m", "1y", "3y", "5y", "all" };

    // Client-supplied history is untrusted: an unbounded role string or message length would let a
    // caller smuggle fabricated "instructions" into what the model reads as prior conversation.
    private static IReadOnlyList<AiChatMessage> SanitizeHistory(IReadOnlyList<AiChatMessage>? history)
    {
        if (history == null || history.Count == 0) return [];
        return history
            .TakeLast(MaxHistoryTurns)
            .Where(message => message.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(message.Content))
            .Select(message => message with
            {
                Content = message.Content.Length > MaxHistoryMessageLength ? message.Content[..MaxHistoryMessageLength] : message.Content
            })
            .ToList();
    }

    // Conversation state may be echoed by a client, so every field is bounded here. Ids are only
    // hints: action validation re-reads each one for this user before it can be targeted.
    internal static AiConversationState? SanitizeConversationState(AiConversationState? state)
    {
        if (state == null) return null;
        static string? Reference(string? value) =>
            string.IsNullOrWhiteSpace(value) || value.Trim().Length > MaxStateReferenceLength ? null : value.Trim();

        var ids = state.LastMatchedTransactionIds?
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 64)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxStateMatchedIds)
            .ToList();
        var cycleKey = Reference(state.LastReportCycleKey) is { } key &&
            Regex.IsMatch(key, @"^(?:19|20)\d{2}-(?:0[1-9]|1[0-2])$")
                ? key
                : null;
        var range = Reference(state.LastInvestmentRange) is { } value && StateInvestmentRanges.Contains(value)
            ? value.ToLower(CultureInfo.InvariantCulture)
            : null;
        // Not clamped to the reference length: it is a whole request, and truncating it would hand
        // back half an instruction for the next turn to carry out.
        var pending = string.IsNullOrWhiteSpace(state.PendingLedgerRequest)
            ? null
            : state.PendingLedgerRequest.Trim() is { Length: <= MaxMessageLength } request ? request : null;

        return new AiConversationState(
            ids is { Count: > 0 } ? ids : null,
            state.LastSavingsGoalId is > 0 ? state.LastSavingsGoalId : null,
            range,
            cycleKey,
            Reference(state.LastLoanId),
            pending);
    }
}
