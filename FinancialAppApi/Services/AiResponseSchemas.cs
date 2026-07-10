namespace FinancialAppApi.Services;

internal static class AiResponseSchemas
{
    public static readonly object Chat = Obj(
        new Dictionary<string, object>
        {
            ["reply"] = Str("Concise user-facing reply."),
            ["closeChat"] = Bool(),
            ["actions"] = Arr(Obj(
                new Dictionary<string, object>
                {
                    ["type"] = Str(enums:
                    [
                        "openLedger", "openDashboard", "openRecurring", "openWishlist", "openSettings",
                        "openAddLedgerDraft", "openAddRecurringDraft", "openAddWishlistDraft",
                        "openEditLedgerDraft", "openEditRecurringDraft", "openEditWishlistDraft"
                    ]),
                    ["payload"] = ActionPayload()
                },
                ["type", "payload"]), maxItems: 3)
        },
        ["reply", "closeChat", "actions"]);

    public static object CategorySuggestions(IReadOnlyList<string> categories) => Obj(
        new Dictionary<string, object>
        {
            ["suggestions"] = Arr(Obj(
                new Dictionary<string, object>
                {
                    ["category"] = Str(enums: categories),
                    ["confidence"] = Num("Confidence from 0 to 1.", 0, 1)
                },
                ["category", "confidence"]), maxItems: 3)
        },
        ["suggestions"]);

    public static readonly object NoteSuggestions = Obj(
        new Dictionary<string, object>
        {
            ["notes"] = Arr(Obj(
                new Dictionary<string, object>
                {
                    ["note"] = Str("Clean transaction description, at most 70 characters."),
                    ["reason"] = Str("Very short reason for this alternative.")
                },
                ["note", "reason"]), minItems: 3, maxItems: 3)
        },
        ["notes"]);

    public static readonly object CategoryCleanup = Obj(
        new Dictionary<string, object>
        {
            ["suggestions"] = Arr(Obj(
                new Dictionary<string, object>
                {
                    ["type"] = Str(enums: ["delete", "merge", "add", "consolidate"]),
                    ["title"] = Str("Short title."),
                    ["summary"] = Str("One-sentence reason."),
                    ["categories"] = Arr(Str("Existing category name.")),
                    ["targetCategory"] = NullableString("Existing destination category for merge; null otherwise."),
                    ["newCategoryName"] = NullableString("New category for add; null otherwise."),
                    ["confidence"] = Num("Confidence from 0 to 1.", 0, 1)
                },
                ["type", "title", "summary", "categories", "targetCategory", "newCategoryName", "confidence"]),
                maxItems: 5)
        },
        ["suggestions"]);

    public static object Receipt(IReadOnlyList<string> categories) => Obj(
        new Dictionary<string, object>
        {
            ["description"] = Str("Merchant name or short purchase description."),
            ["amount"] = NullableNumber("Final non-negative total paid."),
            ["date"] = NullableString("Visible receipt date in YYYY-MM-DD format; null if unclear."),
            ["category"] = Str(enums: categories),
            ["ledgerCategory"] = Str(enums: ["Essentials", "Growth", "Stability", "Rewards"]),
            ["confidence"] = Num("Overall extraction confidence from 0 to 1.", 0, 1)
        },
        ["description", "amount", "date", "category", "ledgerCategory", "confidence"]);

    private static object ActionPayload() => Obj(new Dictionary<string, object>
    {
        ["id"] = Str(),
        ["month"] = Str(),
        ["year"] = Int(),
        ["allCycles"] = Bool(),
        ["category"] = Str(),
        ["ledgerCategory"] = Str(),
        ["txType"] = Str(enums: ["inflow", "outflow", "transfer"]),
        ["search"] = Str(),
        ["date"] = Str(),
        ["description"] = Str(),
        ["name"] = Str(),
        ["amount"] = Num(),
        ["price"] = Num(),
        ["priority"] = Str(),
        ["isActive"] = Bool(),
        ["startDate"] = Str(),
        ["endDate"] = Str(),
        ["changes"] = Obj(new Dictionary<string, object>
        {
            ["description"] = Str(), ["name"] = Str(), ["amount"] = Num(), ["price"] = Num(),
            ["category"] = Str(), ["ledgerCategory"] = Str(), ["txType"] = Str(), ["date"] = Str(),
            ["priority"] = Str(), ["isActive"] = Bool(), ["startDate"] = Str(), ["endDate"] = Str()
        })
    });

    private static Dictionary<string, object> Obj(Dictionary<string, object> properties, string[]? required = null)
    {
        var schema = new Dictionary<string, object> { ["type"] = "object", ["properties"] = properties };
        if (required is { Length: > 0 }) schema["required"] = required;
        return schema;
    }

    private static Dictionary<string, object> Arr(object items, int? minItems = null, int? maxItems = null)
    {
        var schema = new Dictionary<string, object> { ["type"] = "array", ["items"] = items };
        if (minItems.HasValue) schema["minItems"] = minItems.Value;
        if (maxItems.HasValue) schema["maxItems"] = maxItems.Value;
        return schema;
    }

    private static Dictionary<string, object> Str(string? description = null, IReadOnlyList<string>? enums = null)
    {
        var schema = new Dictionary<string, object> { ["type"] = "string" };
        if (description != null) schema["description"] = description;
        if (enums is { Count: > 0 }) schema["enum"] = enums;
        return schema;
    }

    private static Dictionary<string, object> NullableString(string description) => new()
    {
        ["type"] = new[] { "string", "null" }, ["description"] = description
    };

    private static Dictionary<string, object> Num(string? description = null, double? minimum = null, double? maximum = null)
    {
        var schema = new Dictionary<string, object> { ["type"] = "number" };
        if (description != null) schema["description"] = description;
        if (minimum.HasValue) schema["minimum"] = minimum.Value;
        if (maximum.HasValue) schema["maximum"] = maximum.Value;
        return schema;
    }

    private static Dictionary<string, object> NullableNumber(string description) => new()
    {
        ["type"] = new[] { "number", "null" }, ["description"] = description
    };

    private static Dictionary<string, object> Int() => new() { ["type"] = "integer" };
    private static Dictionary<string, object> Bool() => new() { ["type"] = "boolean" };
}
