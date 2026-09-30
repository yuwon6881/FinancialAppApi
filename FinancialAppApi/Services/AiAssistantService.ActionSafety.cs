using System.Text.RegularExpressions;
using System.Text.Json;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    // Tracks what earlier actions in the same turn already claimed.
    internal sealed class ActionValidationState
    {
        public bool NonLedgerMutationClaimed { get; set; }
    }

    // One proposed action through every server rule. The rejection reason is phrased for the
    // model, so the tool-calling engine can hand it back and let the model correct itself.
    // Mutation intent is read only from the user's own message, never from tool output, so text
    // planted in a transaction description cannot authorise a change.
    private async Task<(AiUiAction? Action, string? Rejection)> ValidateActionAsync(
        JsonElement actionEl,
        AiContext context,
        string userMessage,
        AiConstraints constraints,
        ActionValidationState state,
        CancellationToken cancellationToken)
    {
        if (actionEl.ValueKind != JsonValueKind.Object || !actionEl.TryGetProperty("type", out var typeProp) ||
            typeProp.ValueKind != JsonValueKind.String)
            return (null, "Each action needs a string type.");
        var type = typeProp.GetString() ?? "";
        if (!AllowedActionTypes.Contains(type)) return (null, $"'{type}' is not an available action.");
        var payload = actionEl.TryGetProperty("payload", out var payloadProp) && payloadProp.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(payloadProp.GetRawText()) ?? []
            : [];
        if (context.SensitiveMode && (IsMutationAction(type) || type.Equals("openLedgerExport", StringComparison.OrdinalIgnoreCase)))
            return (null, "Sensitive mode is on, so records cannot be changed or exported. Tell the user to unhide balances first.");
        if (IsQuestionOnlyRequest(userMessage) && type.Equals("openLedger", StringComparison.OrdinalIgnoreCase))
            return (null, "The user asked a question; answer it instead of opening the ledger.");
        if (IsMutationAction(type) && constraints.Hypothetical)
            return (null, "The user is asking hypothetically, not requesting a change.");
        if (IsMutationAction(type) && LooksLikeNegatedMutation(userMessage))
            return (null, "The user said not to make this change.");
        if (IsMutationAction(type) && !HasExplicitMutationCommand(userMessage, type))
            return (null, "The user's own message does not ask for this change. Only propose a change the user explicitly requested.");
        // "Don't open the ledger" -> honor the negation deterministically; drop every
        // navigation action regardless of what the model chose to return.
        if (constraints.PreventNavigation && IsNavigationAction(type))
            return (null, "The user asked not to be taken to another screen.");
        if (context.SensitiveMode)
        {
            RemoveSensitivePayloadFields(payload);
        }
        if (!IsActionSafe(type, payload, context))
            return (null, "The payload is invalid, or it names a record id, category, or account that no tool returned in this turn. Look the record up first and copy its exact id.");
        if (!await IsDatabaseActionSafeAsync(type, payload, cancellationToken))
            return (null, "The app would refuse this change (for example a loan-linked bill, an unaffordable or already-claimed reward, or an inactive goal).");
        if (IsMutationAction(type) &&
            !type.Equals("openAddLedgerDraft", StringComparison.OrdinalIgnoreCase))
        {
            if (state.NonLedgerMutationClaimed) return (null, "Only one change can be proposed per message.");
            state.NonLedgerMutationClaimed = true;
        }
        return (new AiUiAction(type, payload), null);
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

        return !NavigationRequestSignal.IsMatch(lower);
    }

    // Word-bounded, so a request that ends the sentence ("can you show me?", "open it?") still
    // counts; the old substring checks needed a trailing space and turned those into questions.
    private static readonly Regex NavigationRequestSignal = new(
        @"\b(?:open|show|display|view|go to|navigate|filter|filtered|take me|bring me|redirect|pull (?:it |them )?up|jump to)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
