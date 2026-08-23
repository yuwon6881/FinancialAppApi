using System.Text.RegularExpressions;
using System.Text.Json;
using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
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
}
