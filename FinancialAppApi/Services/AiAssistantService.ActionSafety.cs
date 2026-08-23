using System.Text.RegularExpressions;
using System.Text.Json;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private async Task<AiChatResponse> ParseAndValidateResponseAsync(
        string text,
        AiContext context,
        string userMessage,
        AiConstraints constraints,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var reply = root.TryGetProperty("reply", out var replyProp) ? replyProp.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(reply)) reply = "I'm unable to perform that action.";

        var actions = new List<AiUiAction>();
        if (root.TryGetProperty("actions", out var actionsProp) && actionsProp.ValueKind == JsonValueKind.Array)
        {
            // Ledger-add is the only intent allowed to return more than one action (one flat draft
            // per record). Its ceiling is MaxChatActions, which is bounded by what the chat model's
            // structured-output budget accepts -- see AiResponseSchemas.MaxChatActions.
            var returnedActions = actionsProp.EnumerateArray().Take(AiResponseSchemas.MaxChatActions).ToList();
            var nonLedgerMutationClaimed = false;

            foreach (var actionEl in returnedActions)
            {
                if (!actionEl.TryGetProperty("type", out var typeProp)) continue;
                var type = typeProp.GetString() ?? "";
                if (!AllowedActionTypes.Contains(type)) continue;
                var payload = actionEl.TryGetProperty("payload", out var payloadProp) && payloadProp.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<Dictionary<string, object?>>(payloadProp.GetRawText()) ?? []
                    : [];
                if (context.SensitiveMode && (IsMutationAction(type) || type.Equals("openLedgerExport", StringComparison.OrdinalIgnoreCase))) continue;
                if (IsQuestionOnlyRequest(userMessage) && type.Equals("openLedger", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsMutationAction(type) &&
                    (constraints.Hypothetical || LooksLikeNegatedMutation(userMessage) ||
                     !HasExplicitMutationCommand(userMessage, type))) continue;
                // "Don't open the ledger" -> honor the negation deterministically; drop every
                // navigation action regardless of what the model chose to return.
                if (constraints.PreventNavigation && IsNavigationAction(type)) continue;
                if (context.SensitiveMode)
                {
                    RemoveSensitivePayloadFields(payload);
                }
                if (!IsActionSafe(type, payload, context) ||
                    !await IsDatabaseActionSafeAsync(type, payload, cancellationToken)) continue;
                if (IsMutationAction(type) &&
                    !type.Equals("openAddLedgerDraft", StringComparison.OrdinalIgnoreCase))
                {
                    if (nonLedgerMutationClaimed) continue;
                    nonLedgerMutationClaimed = true;
                }
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
        payload.Remove("minAmount");
        payload.Remove("maxAmount");
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
        "openLedger", "openDashboard", "openRecurring", "openWishlist", "openReports", "openInvestments", "openLedgerExport"
    };

    private static readonly HashSet<string> MutationActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openAddLedgerDraft", "openEditLedgerDraft", "openAddRecurringDraft", "openEditRecurringDraft", "openAddWishlistDraft", "openEditWishlistDraft",
        "requestDeleteLedger", "requestDeleteRecurring", "requestDeleteWishlist",
        "requestConfirmRecurringBill", "requestDiscardRecurringBill",
        "requestPurchaseWishlist", "requestUnpurchaseWishlist", "toggleRecurring", "updateRecurringReminder"
        , "openAddSavingsGoalDraft", "openEditSavingsGoalDraft"
    };

    // The lead-day choices the Recurring card actually offers. A value outside this set would
    // be silently clamped by the UI, so reject it here instead of applying something the user
    // never asked for.
    private static readonly int[] ReminderLeadDayOptions = [7, 3, 2, 1];

    private static bool IsNavigationAction(string type) => NavigationActionTypes.Contains(type);
    private static bool IsMutationAction(string type) => MutationActionTypes.Contains(type);

    private static readonly Regex NegatedMutationSignal = new(
        @"\b(?:do not|don't|dont|never|not now|without)\b.{0,60}\b(?:add|create|prepare|draft|edit|update|change|delete|remove|erase|purchase|buy|claim|confirm|discard|skip|enable|disable|pause|resume|toggle|remind)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool LooksLikeNegatedMutation(string message) => NegatedMutationSignal.IsMatch(message);

    private static bool HasExplicitMutationCommand(string message, string type)
    {
        if (type.Equals("openAddLedgerDraft", StringComparison.OrdinalIgnoreCase) &&
            CountLedgerDraftListRecords(message) > 0) return true;

        var verbPattern = type switch
        {
            "openAddLedgerDraft" or "openAddRecurringDraft" or "openAddWishlistDraft" or "openAddSavingsGoalDraft" =>
                @"\b(?:add|create|prepare|draft|log|record|transfer|move)\b",
            "openEditLedgerDraft" or "openEditRecurringDraft" or "openEditWishlistDraft" or "openEditSavingsGoalDraft" =>
                @"\b(?:edit|update|change|modify)\b",
            "requestDeleteLedger" or "requestDeleteRecurring" or "requestDeleteWishlist" =>
                @"\b(?:delete|remove|erase)\b",
            "requestConfirmRecurringBill" => @"\b(?:confirm|mark)\b.{0,35}\b(?:paid|payment|bill)\b",
            "requestDiscardRecurringBill" => @"\b(?:discard|skip)\b",
            "requestPurchaseWishlist" => @"\b(?:purchase|buy|claim)\b",
            "requestUnpurchaseWishlist" => @"\b(?:unpurchase|undo|reverse)\b",
            "toggleRecurring" => @"\b(?:enable|disable|activate|deactivate|pause|resume|turn on|turn off|toggle)\b",
            "updateRecurringReminder" => @"\b(?:remind|reminder|notify|notification)\b",
            _ => null
        };
        return verbPattern != null && Regex.IsMatch(message, verbPattern, RegexOptions.IgnoreCase);
    }

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

}
