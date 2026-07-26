namespace FinancialAppApi.Services;

internal static class AiResponseSchemas
{
    // Max number of actions the chat model may return in one turn -- also the effective ceiling
    // on a batched ledger-add (one flat openAddLedgerDraft action per record).
    //
    // WHY 4 (and not 50): gemini-3.5-flash-lite has a small structured-output (responseJsonSchema)
    // complexity budget. Because the actions array items are the full ActionPayload union, Gemini
    // rejects the whole request with 400 INVALID_ARGUMENT once maxItems exceeds ~4. This was
    // measured directly against the live API: full payload passes at maxItems=4 and fails at 5+,
    // and even stripping the payload down does not get a large array under the limit -- the model's
    // budget is the wall. See AiChatSchemaLiveProbe (removed) / commit history for the measurements.
    //
    // TO RAISE THIS: only safe on a model with a larger structured-output budget (a non-lite
    // Gemini tier, or a future model). If AiModels:Chat is pointed at such a model, re-run a schema
    // probe to find the true ceiling, then bump this constant. Do NOT raise it blindly on a lite
    // model -- it silently breaks every chat turn with a 400. When you change it, also update the
    // matching "no more than N ledger draft actions" number in AiAssistantService.BuildSystemInstruction
    // (that prompt is a fixed non-interpolated string, so it can't reference this constant directly).
    internal const int MaxChatActions = 4;

    // Category is enum-constrained to the user's actual categories (like Receipt / CategorySuggestions)
    // so the model is forced to emit a real category name for add/edit drafts and ledger filters --
    // otherwise a free-string guess that doesn't match gets the whole action dropped in
    // AiAssistantService.IsActionSafe. Falls back to a plain string when the caller has no categories.
    public static object Chat(IReadOnlyList<string> categories) => Obj(
        new Dictionary<string, object>
        {
            ["reply"] = Str("Concise user-facing reply."),
            ["closeChat"] = Bool(),
            ["actions"] = Arr(Obj(
                new Dictionary<string, object>
                {
                    ["type"] = Str(enums:
                    [
                        "openLedger", "openDashboard", "openRecurring", "openWishlist",
                        "openAddLedgerDraft", "openAddRecurringDraft", "openAddWishlistDraft",
                        "openEditLedgerDraft", "openEditRecurringDraft", "openEditWishlistDraft",
                        "requestDeleteLedger", "requestDeleteRecurring", "requestDeleteWishlist",
                        "requestConfirmRecurringBill", "requestDiscardRecurringBill",
                        "requestPurchaseWishlist", "requestUnpurchaseWishlist", "toggleRecurring",
                        "openLedgerExport"
                    ]),
                    ["payload"] = ActionPayload(categories)
                },
                ["type", "payload"]), maxItems: MaxChatActions)
        },
        ["reply", "closeChat", "actions"]);

    // Ledger creation uses a dedicated schema instead of the large generic ActionPayload union.
    // This keeps Gemini's structured-output grammar small while making every field needed to
    // stage a local draft mandatory. For an unambiguous line-based list, expectedActionCount pins
    // the response to exactly one action per input line (up to the model-supported ceiling).
    public static object LedgerDraftChat(IReadOnlyList<string> categories, int expectedActionCount = 0)
    {
        var exactCount = expectedActionCount is > 0 and <= MaxChatActions ? expectedActionCount : (int?)null;
        return Obj(
            new Dictionary<string, object>
            {
                ["reply"] = Str("Concise user-facing reply confirming the drafts that will be staged."),
                ["closeChat"] = Bool(),
                ["actions"] = Arr(Obj(
                    new Dictionary<string, object>
                    {
                        ["type"] = Str(enums: ["openAddLedgerDraft"]),
                        ["payload"] = Obj(
                            new Dictionary<string, object>
                            {
                                ["description"] = Str("Transaction description copied from the user's record."),
                                ["amount"] = Num("Positive transaction magnitude."),
                                ["txType"] = Str(enums: ["inflow", "outflow", "transfer"]),
                                ["category"] = Str("Explicit or single most likely normal category.", enums: categories),
                                ["ledgerCategory"] = Str("Essentials unless explicitly requested otherwise.", enums: ["Essentials", "Growth", "Stability", "Rewards"]),
                                ["ledgerCategorySpecified"] = Bool(),
                                ["transferSource"] = Str(enums: ["Essentials", "Growth", "Stability", "Rewards"]),
                                ["transferTarget"] = Str(enums: ["Essentials", "Growth", "Stability", "Rewards"]),
                                ["date"] = Str("Posting date in YYYY-MM-DD when supplied by the user.")
                            },
                            ["description", "amount", "txType", "category", "ledgerCategory", "ledgerCategorySpecified"])
                    },
                    ["type", "payload"]),
                    minItems: exactCount,
                    maxItems: exactCount ?? MaxChatActions)
            },
            ["reply", "closeChat", "actions"]);
    }

    private static readonly IReadOnlyList<string> IntentEnum =
    [
        "ledger.activity_count", "ledger.merchant_search", "ledger.spending_total",
        "ledger.transaction_list", "ledger.comparison", "ledger.edit", "ledger.add",
        "ledger.anomaly", "ledger.duplicates", "wishlist.list", "wishlist.forecast",
        "wishlist.add", "wishlist.edit", "recurring.list", "recurring.upcoming",
        "recurring.add", "recurring.edit", "category_limits.analysis", "cycle.insights",
        "allocation.balance", "allocation.performance",
        "navigation", "general"
    ];

    public static readonly object IntentClassification = Obj(
        new Dictionary<string, object>
        {
            ["intents"] = Arr(Str(enums: IntentEnum), maxItems: 4),
            ["confidence"] = Num("Confidence from 0 to 1.", 0, 1),
            ["entities"] = Obj(
                new Dictionary<string, object>
                {
                    ["searchText"] = NullableString("Activity or merchant text to match; null when not applicable."),
                    ["cycleHint"] = NullableString("Relative or explicit cycle wording; null when not applicable."),
                    ["wishlistReference"] = NullableString("Wishlist item name the user referenced; null otherwise."),
                    ["transactionReference"] = NullableString("Specific transaction the user referenced; null otherwise."),
                    ["category"] = NullableString("Category name the user referenced; null otherwise."),
                    ["ledgerCategory"] = NullableString("Ledger category; null otherwise."),
                    ["date"] = NullableString("Explicit date in YYYY-MM-DD format; null otherwise."),
                    ["amount"] = NullableNumber("Non-negative amount mentioned by the user; null otherwise.")
                },
                ["searchText", "cycleHint", "date", "wishlistReference", "transactionReference", "category", "ledgerCategory", "amount"]),
            ["constraints"] = Obj(
                new Dictionary<string, object>
                {
                    ["preventNavigation"] = Bool(),
                    ["excludeTransfers"] = Bool(),
                    ["exclusions"] = Arr(Str("Category or scope term to exclude.")),
                    ["hypothetical"] = Bool()
                },
                ["preventNavigation", "excludeTransfers", "exclusions", "hypothetical"]),
            ["ambiguities"] = Arr(Str("A brief note about anything ambiguous the classifier could not resolve."))
        },
        ["intents", "confidence", "entities", "constraints", "ambiguities"]);

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
                ["note", "reason"]), minItems: 1, maxItems: 3)
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

    public static readonly object InvestmentActivityScan = Obj(
        new Dictionary<string, object>
        {
            ["type"] = NullableString("Buy, Sell, Dividend, or FeeTax; null if unclear."),
            ["accountId"] = NullableString("Exact available account id; null if unclear."),
            ["instrumentId"] = NullableString("Exact available investment id; null if unclear."),
            ["tradeDate"] = NullableString("Visible activity date in YYYY-MM-DD; null if unclear."),
            ["units"] = NullableNumber("Positive units; null if unclear or not applicable."),
            ["unitPrice"] = NullableNumber("Positive price per unit; null if unclear or not applicable."),
            ["cashAmount"] = NullableNumber("Positive gross amount, dividend, or charge; null if unclear."),
            ["fees"] = NullableNumber("Non-negative fee amount; null if not explicitly supported."),
            ["taxes"] = NullableNumber("Non-negative tax amount; null if not explicitly supported."),
            ["confidence"] = Num("Overall extraction confidence from 0 to 1.", 0, 1)
        },
        ["type", "accountId", "instrumentId", "tradeDate", "units", "unitPrice",
            "cashAmount", "fees", "taxes", "confidence"]);

    private static object ActionPayload(IReadOnlyList<string> categories) => Obj(new Dictionary<string, object>
    {
        ["id"] = Str(),
        ["month"] = Str(),
        ["year"] = Int(),
        ["allCycles"] = Bool(),
        ["range"] = Str(enums: ["monthly", "3month", "6month", "yearly"]),
        ["category"] = Str("Single most fitting category; copy exactly from the App context categories.", enums: categories),
        ["ledgerCategory"] = Str(),
        ["ledgerCategorySpecified"] = Bool(),
        ["txType"] = Str(enums: ["inflow", "outflow", "transfer"]),
        ["transferSource"] = Str(enums: ["Essentials", "Growth", "Stability", "Rewards"]),
        ["transferTarget"] = Str(enums: ["Essentials", "Growth", "Stability", "Rewards"]),
        ["active"] = Bool(),
        ["frequency"] = Str(enums: ["Monthly", "Annually"]),
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
            ["category"] = Str("Single most fitting category; copy exactly from the App context categories.", enums: categories),
            ["ledgerCategory"] = Str(), ["txType"] = Str(), ["date"] = Str(),
            ["frequency"] = Str(enums: ["Monthly", "Annually"]),
            ["transferSource"] = Str(), ["transferTarget"] = Str(),
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
