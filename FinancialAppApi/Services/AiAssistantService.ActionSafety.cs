using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using FinancialAppApi.Database;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private AiChatResponse ParseAndValidateResponse(string text, AiContext context, string userMessage, AiConstraints constraints)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var reply = root.TryGetProperty("reply", out var replyProp) ? replyProp.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(reply)) reply = "I'm unable to perform that action.";

        var actions = new List<AiUiAction>();
        if (root.TryGetProperty("actions", out var actionsProp) && actionsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var actionEl in actionsProp.EnumerateArray().Take(3))
            {
                if (!actionEl.TryGetProperty("type", out var typeProp)) continue;
                var type = typeProp.GetString() ?? "";
                if (!AllowedActionTypes.Contains(type)) continue;
                var payload = actionEl.TryGetProperty("payload", out var payloadProp) && payloadProp.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<Dictionary<string, object?>>(payloadProp.GetRawText()) ?? []
                    : [];
                if (context.SensitiveMode && (IsMutationAction(type) || type.Equals("openLedgerExport", StringComparison.OrdinalIgnoreCase))) continue;
                if (IsQuestionOnlyRequest(userMessage) && type.Equals("openLedger", StringComparison.OrdinalIgnoreCase)) continue;
                // "Don't open the ledger" -> honor the negation deterministically; drop every
                // navigation action regardless of what the model chose to return.
                if (constraints.PreventNavigation && IsNavigationAction(type)) continue;
                if (context.SensitiveMode)
                {
                    RemoveSensitivePayloadFields(payload);
                }
                if (!IsActionSafe(type, payload, context)) continue;
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
        "openLedger", "openDashboard", "openRecurring", "openWishlist", "openLedgerExport"
    };

    private static readonly HashSet<string> MutationActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openEditLedgerDraft", "openEditRecurringDraft", "openEditWishlistDraft",
        "requestDeleteLedger", "requestDeleteRecurring", "requestDeleteWishlist",
        "requestConfirmRecurringBill", "requestDiscardRecurringBill",
        "requestPurchaseWishlist", "requestUnpurchaseWishlist", "toggleRecurring"
    };

    private static bool IsNavigationAction(string type) => NavigationActionTypes.Contains(type);
    private static bool IsMutationAction(string type) => MutationActionTypes.Contains(type);

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

    private static bool IsActionSafe(string type, Dictionary<string, object?> payload, AiContext context)
    {
        if (!HasKnownOptionalString(payload, "category", context.Categories) ||
            !HasKnownOptionalString(payload, "ledgerCategory", context.LedgerCategories) ||
            !HasKnownOptionalString(payload, "txType", ["inflow", "outflow", "transfer"]) ||
            !HasKnownOptionalString(payload, "transferSource", ["Essentials", "Growth", "Stability", "Rewards"]) ||
            !HasKnownOptionalString(payload, "transferTarget", ["Essentials", "Growth", "Stability", "Rewards"]) ||
            !HasKnownOptionalString(payload, "range", ["monthly", "3month", "6month", "yearly"]) ||
            !HasKnownOptionalString(payload, "month", FinancialConstants.MonthAbbreviations) ||
            !HasValidOptionalInteger(payload, "year", 1900, 2100) ||
            !HasValidOptionalNonNegativeNumber(payload, "amount") ||
            !HasValidOptionalNonNegativeNumber(payload, "price") ||
            !HasValidOptionalIsoDate(payload, "date") ||
            !HasValidOptionalIsoDate(payload, "startDate") ||
            !HasValidOptionalIsoDate(payload, "endDate"))
        {
            return false;
        }

        if (payload.TryGetValue("changes", out var changesValue) && changesValue != null)
        {
            var changes = changesValue is JsonElement element && element.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText())
                : changesValue as Dictionary<string, object?>;
            if (changes == null ||
                !HasKnownOptionalString(changes, "category", context.Categories) ||
                !HasKnownOptionalString(changes, "ledgerCategory", context.LedgerCategories) ||
                !HasKnownOptionalString(changes, "txType", ["inflow", "outflow", "transfer"]) ||
                !HasKnownOptionalString(changes, "transferSource", ["Essentials", "Growth", "Stability", "Rewards"]) ||
                !HasKnownOptionalString(changes, "transferTarget", ["Essentials", "Growth", "Stability", "Rewards"]) ||
                !HasValidOptionalNonNegativeNumber(changes, "amount") ||
                !HasValidOptionalNonNegativeNumber(changes, "price") ||
                !HasValidOptionalIsoDate(changes, "date") ||
                !HasValidOptionalIsoDate(changes, "startDate") ||
                !HasValidOptionalIsoDate(changes, "endDate"))
            {
                return false;
            }
        }

        if (type.Equals("openAddLedgerDraft", StringComparison.OrdinalIgnoreCase) &&
            ReadPayloadString(payload, "txType")?.Equals("transfer", StringComparison.OrdinalIgnoreCase) == true)
        {
            var source = ReadPayloadString(payload, "transferSource");
            var target = ReadPayloadString(payload, "transferTarget");
            if (source == null || target == null || source.Equals(target, StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (type.Equals("toggleRecurring", StringComparison.OrdinalIgnoreCase) && !HasBoolean(payload, "active")) return false;

        if (type.Equals("openEditRecurringDraft", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("requestDeleteRecurring", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("requestConfirmRecurringBill", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("requestDiscardRecurringBill", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("toggleRecurring", StringComparison.OrdinalIgnoreCase))
        {
            return HasKnownId(payload, "id", context.RecurringPayments);
        }
        if (type.Equals("requestPurchaseWishlist", StringComparison.OrdinalIgnoreCase))
        {
            return HasKnownIdWithBoolean(payload, "id", context.WishlistItems, "IsPurchased", false);
        }
        if (type.Equals("requestUnpurchaseWishlist", StringComparison.OrdinalIgnoreCase))
        {
            return HasKnownIdWithBoolean(payload, "id", context.WishlistItems, "IsPurchased", true);
        }
        if (type.Equals("openEditWishlistDraft", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("requestDeleteWishlist", StringComparison.OrdinalIgnoreCase))
        {
            return HasKnownId(payload, "id", context.WishlistItems);
        }
        if (type.Equals("openEditLedgerDraft", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("requestDeleteLedger", StringComparison.OrdinalIgnoreCase))
        {
            return HasKnownId(payload, "id", context.RecentTransactions);
        }
        return true;
    }

    private static bool LooksLikeProtectedMutationCommand(string message)
    {
        var lower = message.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower)) return false;
        if (lower.StartsWith("what should ") || lower.StartsWith("which should ") ||
            lower.StartsWith("why should ") || lower.StartsWith("how should ")) return false;
        return Regex.IsMatch(lower,
            @"\b(edit|update|modify|delete|remove|erase|purchase|claim|unpurchase|undo (?:the )?purchase|mark (?:it |this |the .+ )?(?:as )?paid|confirm (?:it |this |the .+ )?(?:as )?paid|discard|skip|enable|disable|turn on|turn off|activate|deactivate|toggle)\b",
            RegexOptions.IgnoreCase);
    }

    private static string? ReadPayloadString(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value == null) return null;
        return value is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : value as string;
    }

    private static bool HasBoolean(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value == null) return false;
        return value is bool || value is JsonElement element &&
            element.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool HasKnownOptionalString(
        IReadOnlyDictionary<string, object?> payload,
        string key,
        IReadOnlyCollection<string> allowed)
    {
        if (!payload.TryGetValue(key, out var value) || value == null) return true;
        var text = value is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : value as string;
        return !string.IsNullOrWhiteSpace(text) && allowed.Contains(text, StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasValidOptionalNonNegativeNumber(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value == null) return true;
        double number;
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Number)
        {
            if (!element.TryGetDouble(out number)) return false;
        }
        else if (!double.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }
        return double.IsFinite(number) && number >= 0;
    }

    private static bool HasValidOptionalInteger(
        IReadOnlyDictionary<string, object?> payload,
        string key,
        int minimum,
        int maximum)
    {
        if (!payload.TryGetValue(key, out var value) || value == null) return true;
        int number;
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Number)
        {
            if (!element.TryGetInt32(out number)) return false;
        }
        else if (!int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }
        return number >= minimum && number <= maximum;
    }

    private static bool HasValidOptionalIsoDate(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value == null) return true;
        var text = value is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : value as string;
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static bool HasKnownId(Dictionary<string, object?> payload, string key, object records)
    {
        if (!payload.TryGetValue(key, out var idObj) || idObj == null) return false;
        var id = idObj.ToString();
        if (string.IsNullOrWhiteSpace(id)) return false;
        var json = JsonSerializer.Serialize(records);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array &&
            doc.RootElement.EnumerateArray().Any(e =>
                (e.TryGetProperty("id", out var p) || e.TryGetProperty("Id", out p)) &&
                string.Equals(p.ToString(), id, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasKnownIdWithBoolean(
        Dictionary<string, object?> payload,
        string key,
        object records,
        string booleanProperty,
        bool expected)
    {
        if (!payload.TryGetValue(key, out var idObj) || idObj == null) return false;
        var id = idObj.ToString();
        if (string.IsNullOrWhiteSpace(id)) return false;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(records));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
        var camelProperty = char.ToLowerInvariant(booleanProperty[0]) + booleanProperty[1..];
        foreach (var record in doc.RootElement.EnumerateArray())
        {
            if (!(record.TryGetProperty("id", out var recordId) || record.TryGetProperty("Id", out recordId)) ||
                !string.Equals(recordId.ToString(), id, StringComparison.OrdinalIgnoreCase)) continue;
            if (!(record.TryGetProperty(booleanProperty, out var booleanValue) || record.TryGetProperty(camelProperty, out booleanValue))) return false;
            return booleanValue.ValueKind is JsonValueKind.True or JsonValueKind.False && booleanValue.GetBoolean() == expected;
        }
        return false;
    }
}
