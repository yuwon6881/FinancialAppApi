using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
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

        ApplyDefaultTransferDescription(payload, context);
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
            var buckets = new[] { "Essentials", "Growth", "Stability", "Rewards" };
            if (source == null || target == null ||
                !buckets.Contains(source, StringComparer.OrdinalIgnoreCase) ||
                !buckets.Contains(target, StringComparer.OrdinalIgnoreCase) ||
                !HasValidAccountPlacement(record, context, txType, source, target)) return false;
            // Same bucket is an internal account move, which is only meaningful once both ends are
            // named: the bucket total does not change, the money has only changed hands. Without
            // two exact accounts there is nothing to move between, so it stays rejected.
            return source.Equals(target, StringComparison.OrdinalIgnoreCase)
                ? IsExplicitAccountMove(record)
                : true;
        }

        var normalCategories = context.Categories
            .Where(category => !TransactionCategoryService.IsReservedName(category))
            .ToList();
        var ledgerCategory = ReadPayloadString(record, "ledgerCategory");
        return !HasUnknownOrMissingString(record, "category", normalCategories) &&
            !HasUnknownOrMissingString(record, "ledgerCategory", ["Essentials", "Growth", "Stability", "Rewards", "Income"]) &&
            HasValidAccountPlacement(record, context, txType, ledgerCategory, null);
    }

    private static bool IsExplicitAccountMove(IReadOnlyDictionary<string, object?> record)
    {
        var accountId = ReadPayloadString(record, "accountId");
        var counterAccountId = ReadPayloadString(record, "counterAccountId");
        return accountId is { Length: > 0 } && counterAccountId is { Length: > 0 } &&
            !accountId.Equals(counterAccountId, StringComparison.Ordinal);
    }

    private static bool HasValidAccountPlacement(
        IReadOnlyDictionary<string, object?> record,
        AiContext context,
        string txType,
        string? sourceOrLedger,
        string? target)
    {
        var accountId = ReadPayloadString(record, "accountId");
        var counterAccountId = ReadPayloadString(record, "counterAccountId");
        if (accountId == null && counterAccountId == null) return true;
        var accounts = context.LedgerAccountContext?.Accounts;
        if (accounts == null) return false;
        AiLedgerAccountRow? Find(string? id) => id == null
            ? null
            : accounts.FirstOrDefault(account => account.Id.Equals(id, StringComparison.Ordinal));
        var account = Find(accountId);
        var counter = Find(counterAccountId);
        if (accountId != null && (account == null || account.IsArchived)) return false;
        if (counterAccountId != null && (counter == null || counter.IsArchived)) return false;
        if (txType.Equals("transfer", StringComparison.OrdinalIgnoreCase))
        {
            return (account == null || account.Bucket.Equals(sourceOrLedger, StringComparison.OrdinalIgnoreCase)) &&
                (counter == null || counter.Bucket.Equals(target, StringComparison.OrdinalIgnoreCase));
        }
        if (string.Equals(sourceOrLedger, "Income", StringComparison.OrdinalIgnoreCase)) return account == null && counter == null;
        return counter == null && (account == null || account.Bucket.Equals(sourceOrLedger, StringComparison.OrdinalIgnoreCase));
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
