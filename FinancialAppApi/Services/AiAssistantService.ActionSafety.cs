using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

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
            var containsOnlyLedgerDrafts = returnedActions.Count > 0 && returnedActions.All(actionEl =>
                actionEl.ValueKind == JsonValueKind.Object &&
                actionEl.TryGetProperty("type", out var actionType) &&
                actionType.ValueKind == JsonValueKind.String &&
                actionType.GetString()?.Equals("openAddLedgerDraft", StringComparison.OrdinalIgnoreCase) == true);
            var actionLimit = AiResponseSchemas.MaxChatActions;
            var nonLedgerMutationClaimed = false;

            foreach (var actionEl in returnedActions.Take(actionLimit))
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

    private static bool IsActionSafe(string type, Dictionary<string, object?> payload, AiContext context)
    {
        // Ledger adds only create local drafts. Validate and normalize their dedicated payload
        // before the broad union checks below, which include fields belonging to unrelated action
        // types and can reject an otherwise valid draft when the model emits optional placeholders.
        if (type.Equals("openAddLedgerDraft", StringComparison.OrdinalIgnoreCase))
        {
            return HasValidLedgerDraftPayload(payload, context);
        }

        if (!HasKnownOptionalString(payload, "category", context.Categories) ||
            !HasKnownOptionalString(payload, "ledgerCategory", context.LedgerCategories) ||
            !HasKnownOptionalString(payload, "txType", ["inflow", "outflow", "transfer"]) ||
            !HasKnownOptionalString(payload, "transferSource", ["Essentials", "Growth", "Stability", "Rewards"]) ||
            !HasKnownOptionalString(payload, "transferTarget", ["Essentials", "Growth", "Stability", "Rewards"]) ||
            !HasKnownOptionalString(payload, "frequency", ["Monthly", "Annually"]) ||
            !HasKnownOptionalString(payload, "range", ["monthly", "3month", "6month", "yearly"]) ||
            !HasKnownOptionalString(payload, "month", FinancialConstants.MonthAbbreviations) ||
            !HasValidOptionalInteger(payload, "year", 1900, 2100) ||
            !HasKnownOptionalString(payload, "reminderMode", ["Once", "Daily"]) ||
            !HasValidOptionalNonNegativeNumber(payload, "amount") ||
            !HasValidOptionalNonNegativeNumber(payload, "price") ||
            !HasValidOptionalNonNegativeNumber(payload, "minAmount") ||
            !HasValidOptionalNonNegativeNumber(payload, "maxAmount") ||
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
                !HasKnownOptionalString(changes, "frequency", ["Monthly", "Annually"]) ||
                !HasValidOptionalNonNegativeNumber(changes, "amount") ||
                !HasValidOptionalNonNegativeNumber(changes, "price") ||
                !HasValidOptionalIsoDate(changes, "date") ||
                !HasValidOptionalIsoDate(changes, "startDate") ||
                !HasValidOptionalIsoDate(changes, "endDate"))
            {
                return false;
            }
        }

        // An inverted amount range matches nothing and the filter bar refuses to apply it, so a
        // reversed pair is a mistake to reject rather than something to hand to the ledger.
        if (ReadPayloadNumber(payload, "minAmount") is { } minAmount &&
            ReadPayloadNumber(payload, "maxAmount") is { } maxAmount &&
            minAmount > maxAmount)
        {
            return false;
        }

        if (type.Equals("toggleRecurring", StringComparison.OrdinalIgnoreCase) && !HasBoolean(payload, "active")) return false;

        if (type.Equals("openReports", StringComparison.OrdinalIgnoreCase))
        {
            return HasOnlyKeys(payload, ["cycleKey"]) && (!payload.TryGetValue("cycleKey", out var cycleKey) ||
                cycleKey == null || (cycleKey is JsonElement element && element.ValueKind == JsonValueKind.Null) ||
                cycleKey.ToString() is { } key && Regex.IsMatch(key, @"^(?:19|20)\d{2}-(?:0[1-9]|1[0-2])$"));
        }
        if (type.Equals("openInvestments", StringComparison.OrdinalIgnoreCase)) return payload.Count == 0;
        if (type.Equals("openWishlist", StringComparison.OrdinalIgnoreCase))
        {
            return HasOnlyKeys(payload, ["savingsGoalId"]) && (!payload.ContainsKey("savingsGoalId") || HasPositiveInteger(payload, "savingsGoalId"));
        }
        if (type.Equals("openAddSavingsGoalDraft", StringComparison.OrdinalIgnoreCase))
        {
            return HasOnlyKeys(payload, ["name", "targetAmount", "targetDate", "priority", "isRecurring", "recurrenceMonths", "fundingBucket"]) &&
                HasRequiredString(payload, "name") && HasRequiredPositiveNumber(payload, "targetAmount") &&
                HasRequiredIsoDate(payload, "targetDate") && HasRequiredKnownString(payload, "priority", ["low", "medium", "high"]) &&
                HasBoolean(payload, "isRecurring") && HasRequiredInteger(payload, "recurrenceMonths", 0, 120) &&
                HasKnownOptionalString(payload, "fundingBucket", ["Essentials", "Rewards"]);
        }

        if (type.Equals("openAddRecurringDraft", StringComparison.OrdinalIgnoreCase))
        {
            return HasOnlyKeys(payload, ["name", "amount", "category", "ledgerCategory", "frequency", "startDate", "endDate"]) &&
                HasRequiredString(payload, "name") && HasRequiredPositiveNumber(payload, "amount") &&
                HasRequiredString(payload, "category") && HasRequiredKnownString(payload, "ledgerCategory", context.LedgerCategories) &&
                HasRequiredKnownString(payload, "frequency", ["Monthly", "Annually"]) &&
                HasRequiredIsoDate(payload, "startDate");
        }
        if (type.Equals("openAddWishlistDraft", StringComparison.OrdinalIgnoreCase))
        {
            return HasOnlyKeys(payload, ["name", "price", "priority", "isActive"]) &&
                HasRequiredString(payload, "name") && HasRequiredPositiveNumber(payload, "price") &&
                HasRequiredKnownString(payload, "priority", ["low", "medium", "high"]) &&
                HasBoolean(payload, "isActive");
        }
        if (type.Equals("openEditSavingsGoalDraft", StringComparison.OrdinalIgnoreCase))
        {
            return HasOnlyKeys(payload, ["id", "changes"]) && HasPositiveInteger(payload, "id") && HasValidSavingsGoalChanges(payload);
        }

        if (type.Equals("updateRecurringReminder", StringComparison.OrdinalIgnoreCase))
        {
            if (!HasBoolean(payload, "enabled")) return false;
            // Mode and lead time only exist while the reminder is on; when turning it off the
            // model is expected to omit them rather than invent a pair the user never chose.
            var enabled = ReadPayloadBoolean(payload, "enabled");
            var leadDays = ReadPayloadNumber(payload, "leadDays");
            if (enabled == true &&
                (!HasRequiredKnownString(payload, "reminderMode", ["Once", "Daily"]) ||
                 leadDays == null || leadDays != Math.Truncate(leadDays.Value) ||
                 !ReminderLeadDayOptions.Contains((int)leadDays.Value)))
            {
                return false;
            }
            return HasKnownId(payload, "id", context.RecurringPayments);
        }

        if (type.Equals("requestConfirmRecurringBill", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("requestDiscardRecurringBill", StringComparison.OrdinalIgnoreCase))
        {
            return HasKnownId(payload, "id", context.RecurringPayments) && HasRequiredIsoDate(payload, "date");
        }
        if (type.Equals("requestDeleteRecurring", StringComparison.OrdinalIgnoreCase))
        {
            return context.RecurringPayments is IEnumerable<AiRecurringRow> rows &&
                payload.TryGetValue("id", out var idValue) && idValue != null &&
                rows.Any(row => row.Id.Equals(idValue.ToString(), StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(row.LinkedLoanId));
        }
        if (type.Equals("openEditRecurringDraft", StringComparison.OrdinalIgnoreCase) ||
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

    private async Task<bool> IsDatabaseActionSafeAsync(
        string type,
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        if (type.Equals("requestPurchaseWishlist", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadInteger(payload, "id", out var wishlistId)) return false;
            var wishlistItem = await _context.WishlistItems
                .AsNoTracking()
                .Where(item => item.Id == wishlistId)
                .Select(item => new { item.Price, item.IsPurchased, item.IsActive })
                .SingleOrDefaultAsync(cancellationToken);
            if (wishlistItem == null || wishlistItem.IsPurchased || !wishlistItem.IsActive) return false;
            var pool = await _savingsGoalService.GetPoolSummaryAsync(cancellationToken);
            return pool.Unassigned >= wishlistItem.Price;
        }
        if (type.Equals("requestDeleteRecurring", StringComparison.OrdinalIgnoreCase))
        {
            var recurringId = ReadPayloadString(payload, "id");
            return recurringId != null &&
                !await _context.Loans.AsNoTracking()
                    .AnyAsync(loan => loan.RecurringPaymentId == recurringId, cancellationToken);
        }
        if (!type.Equals("openWishlist", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("openAddSavingsGoalDraft", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("openEditSavingsGoalDraft", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var goals = await _savingsGoalService.GetGoalsAsync(cancellationToken);
        if (type.Equals("openWishlist", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadInteger(payload, "savingsGoalId", out var goalId)) return true;
            return goals.Any(goal => goal.Id == goalId && goal.Status == SavingsGoalStatus.Active);
        }
        if (type.Equals("openAddSavingsGoalDraft", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!TryReadInteger(payload, "id", out var editId)) return false;
        var goal = goals.SingleOrDefault(candidate => candidate.Id == editId);
        return goal != null && goal.Status == SavingsGoalStatus.Active;
    }

    private static bool HasValidLedgerDraftPayload(Dictionary<string, object?> payload, AiContext context)
    {
        // Keep accepting the briefly shipped nested contract so an in-flight response from an
        // older deployment is harmless. New responses use one flat action per record.
        if (payload.TryGetValue("transactions", out var transactionsValue))
        {
            if (transactionsValue is not JsonElement transactions || transactions.ValueKind != JsonValueKind.Array) return false;
            var items = transactions.EnumerateArray().ToList();
            if (items.Count is < 1 or > 50) return false;
            return items.All(item =>
            {
                if (item.ValueKind != JsonValueKind.Object) return false;
                var record = JsonSerializer.Deserialize<Dictionary<string, object?>>(item.GetRawText());
                return record != null && IsValidLedgerDraftRecord(record, context, requireLedgerCategorySpecified: true);
            });
        }

        NormalizeFlatLedgerDraftDefaults(payload, context);
        return IsValidLedgerDraftRecord(payload, context, requireLedgerCategorySpecified: true);
    }

    private static void NormalizeFlatLedgerDraftDefaults(Dictionary<string, object?> payload, AiContext context)
    {
        var txType = ReadPayloadString(payload, "txType");
        if (txType == null || !new[] { "inflow", "outflow", "transfer" }.Contains(txType, StringComparer.OrdinalIgnoreCase))
        {
            txType = "outflow";
        }
        else
        {
            txType = txType.ToLowerInvariant();
        }
        payload["txType"] = txType;

        if (!HasValidOptionalIsoDate(payload, "date")) payload.Remove("date");

        var ledgerCategorySpecified = payload.TryGetValue("ledgerCategorySpecified", out var specifiedValue) &&
            specifiedValue switch
            {
                bool value => value,
                JsonElement element when element.ValueKind is JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
                _ => false
            };

        // ledgerCategorySpecified is an internal safety signal rather than user data. The model may
        // omit optional payload fields even when its reply says the draft was staged, so normalize
        // omission to the safe default instead of silently dropping the entire UI action.
        var isInflow = txType.Equals("inflow", StringComparison.OrdinalIgnoreCase);
        var requestedLedger = ReadPayloadString(payload, "ledgerCategory");
        // Income is only meaningful on an inflow -- it is what drives the income auto-split
        // the manual form applies, and it has no meaning for an outflow or a transfer leg.
        var allowedLedgers = isInflow
            ? new[] { "Essentials", "Growth", "Stability", "Rewards", "Income" }
            : ["Essentials", "Growth", "Stability", "Rewards"];
        var canonicalLedger = allowedLedgers
            .FirstOrDefault(candidate => candidate.Equals(requestedLedger, StringComparison.OrdinalIgnoreCase));
        if (ledgerCategorySpecified && canonicalLedger == null) ledgerCategorySpecified = false;
        payload["ledgerCategorySpecified"] = ledgerCategorySpecified;

        if (!ledgerCategorySpecified && !txType.Equals("transfer", StringComparison.OrdinalIgnoreCase))
        {
            payload["ledgerCategory"] = isInflow ? "Income" : "Essentials";
        }
        else if (canonicalLedger != null)
        {
            payload["ledgerCategory"] = canonicalLedger;
        }

        if (!txType.Equals("transfer", StringComparison.OrdinalIgnoreCase))
        {
            payload.Remove("transferSource");
            payload.Remove("transferTarget");
            var normalCategories = context.Categories
                .Where(category => !TransactionCategoryService.IsReservedName(category))
                .ToList();
            var requestedCategory = ReadPayloadString(payload, "category");
            var canonicalCategory = normalCategories.FirstOrDefault(category =>
                category.Equals(requestedCategory, StringComparison.OrdinalIgnoreCase));
            canonicalCategory ??= normalCategories.FirstOrDefault(category =>
                category.Equals("Other", StringComparison.OrdinalIgnoreCase));
            canonicalCategory ??= normalCategories.FirstOrDefault();
            if (canonicalCategory != null) payload["category"] = canonicalCategory;
        }
    }

    private static bool IsValidLedgerDraftRecord(
        IReadOnlyDictionary<string, object?> record,
        AiContext context,
        bool requireLedgerCategorySpecified)
    {
        var description = ReadPayloadString(record, "description");
        var txType = ReadPayloadString(record, "txType");
        if (string.IsNullOrWhiteSpace(description) || description.Length > 300 ||
            txType == null || !new[] { "inflow", "outflow", "transfer" }.Contains(txType, StringComparer.OrdinalIgnoreCase) ||
            !HasRequiredPositiveNumber(record, "amount") ||
            !HasValidOptionalIsoDate(record, "date")) return false;

        if (requireLedgerCategorySpecified && !HasBoolean(record, "ledgerCategorySpecified")) return false;

        if (txType.Equals("transfer", StringComparison.OrdinalIgnoreCase))
        {
            var source = ReadPayloadString(record, "transferSource");
            var target = ReadPayloadString(record, "transferTarget");
            return source != null && target != null &&
                new[] { "Essentials", "Growth", "Stability", "Rewards" }.Contains(source, StringComparer.OrdinalIgnoreCase) &&
                new[] { "Essentials", "Growth", "Stability", "Rewards" }.Contains(target, StringComparer.OrdinalIgnoreCase) &&
                !source.Equals(target, StringComparison.OrdinalIgnoreCase);
        }

        var normalCategories = context.Categories
            .Where(category => !TransactionCategoryService.IsReservedName(category))
            .ToList();
        return !HasUnknownOrMissingString(record, "category", normalCategories) &&
            !HasUnknownOrMissingString(record, "ledgerCategory", ["Essentials", "Growth", "Stability", "Rewards", "Income"]);
    }

    private static bool HasUnknownOrMissingString(
        IReadOnlyDictionary<string, object?> payload,
        string key,
        IReadOnlyCollection<string> allowed)
    {
        var value = ReadPayloadString(payload, key);
        return string.IsNullOrWhiteSpace(value) || !allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasRequiredPositiveNumber(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.ContainsKey(key) || !HasValidOptionalNonNegativeNumber(payload, key)) return false;
        var value = payload[key];
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Number)
            return element.TryGetDouble(out var jsonNumber) && jsonNumber > 0;
        return double.TryParse(value?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number) && number > 0;
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

    private static double? ReadPayloadNumber(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value == null) return null;
        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var jsonNumber)
                ? jsonNumber
                : null;
        }
        return double.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    private static bool HasBoolean(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value == null) return false;
        return value is bool || value is JsonElement element &&
            element.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool? ReadPayloadBoolean(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value == null) return null;
        if (value is bool boolean) return boolean;
        if (value is JsonElement element && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return element.GetBoolean();
        return null;
    }

    private static bool HasRequiredString(IReadOnlyDictionary<string, object?> payload, string key)
    {
        var value = ReadPayloadString(payload, key);
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 200;
    }

    private static bool HasRequiredKnownString(IReadOnlyDictionary<string, object?> payload, string key, IReadOnlyCollection<string> allowed)
    {
        var value = ReadPayloadString(payload, key);
        return value != null && allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasOnlyKeys(IReadOnlyDictionary<string, object?> payload, string[] allowed) =>
        payload.Keys.All(allowed.Contains);

    private static bool HasRequiredIsoDate(IReadOnlyDictionary<string, object?> payload, string key) =>
        payload.ContainsKey(key) && HasValidOptionalIsoDate(payload, key) && !string.IsNullOrWhiteSpace(ReadPayloadString(payload, key));

    private static bool HasRequiredInteger(IReadOnlyDictionary<string, object?> payload, string key, int minimum, int maximum) =>
        payload.ContainsKey(key) && HasValidOptionalInteger(payload, key, minimum, maximum);

    private static bool HasPositiveInteger(IReadOnlyDictionary<string, object?> payload, string key) =>
        TryReadInteger(payload, key, out var value) && value > 0;

    private static bool TryReadInteger(IReadOnlyDictionary<string, object?> payload, string key, out int value)
    {
        value = 0;
        if (!payload.TryGetValue(key, out var raw) || raw == null) return false;
        if (raw is JsonElement element && element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt32(out value);
        return int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool HasValidSavingsGoalChanges(IReadOnlyDictionary<string, object?> payload)
    {
        if (!payload.TryGetValue("changes", out var raw) || raw == null) return false;
        Dictionary<string, object?> changes;
        if (raw is JsonElement element && element.ValueKind == JsonValueKind.Object)
            changes = JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText()) ?? [];
        else if (raw is Dictionary<string, object?> dictionary)
            changes = dictionary;
        else
            return false;
        if (changes.Count == 0 || !changes.Keys.All(new[] { "name", "targetAmount", "targetDate", "priority", "isRecurring", "recurrenceMonths", "fundingBucket" }.Contains)) return false;
        return (!changes.ContainsKey("name") || HasRequiredString(changes, "name")) &&
            (!changes.ContainsKey("targetAmount") || HasRequiredPositiveNumber(changes, "targetAmount")) &&
            (!changes.ContainsKey("targetDate") || HasRequiredIsoDate(changes, "targetDate")) &&
            (!changes.ContainsKey("priority") || HasRequiredKnownString(changes, "priority", ["low", "medium", "high"])) &&
            (!changes.ContainsKey("isRecurring") || HasBoolean(changes, "isRecurring")) &&
            (!changes.ContainsKey("recurrenceMonths") || HasRequiredInteger(changes, "recurrenceMonths", 0, 120)) &&
            HasKnownOptionalString(changes, "fundingBucket", ["Essentials", "Rewards"]);
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
        if (records is not System.Collections.IEnumerable collection) return false;

        foreach (var item in collection)
        {
            if (item == null) continue;
            var type = item.GetType();
            var prop = type.GetProperty("Id") ?? type.GetProperty("id");
            if (prop != null)
            {
                var val = prop.GetValue(item)?.ToString();
                if (string.Equals(val, id, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
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
        if (records is not System.Collections.IEnumerable collection) return false;

        var camelProperty = char.ToLowerInvariant(booleanProperty[0]) + booleanProperty[1..];
        foreach (var item in collection)
        {
            if (item == null) continue;
            var type = item.GetType();
            var prop = type.GetProperty("Id") ?? type.GetProperty("id");
            if (prop == null) continue;

            var val = prop.GetValue(item)?.ToString();
            if (string.Equals(val, id, StringComparison.OrdinalIgnoreCase))
            {
                var boolProp = type.GetProperty(booleanProperty) ?? type.GetProperty(camelProperty);
                if (boolProp == null) return false;
                var boolVal = boolProp.GetValue(item);
                return boolVal is bool b && b == expected;
            }
        }
        return false;
    }
}
