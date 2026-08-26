namespace FinancialAppApi.Services;

internal static class AiResponseSchemas
{
    // Max number of actions the chat model may return in one turn -- also the effective ceiling
    // on a batched ledger-add (one flat openAddLedgerDraft action per record).
    //
    // Four actions keeps multi-record drafts reviewable and bounds the amount of UI state staged
    // by one chat turn. When changing it, also update the matching "no more than N ledger draft
    // actions" instruction in AiAssistantService.BuildSystemInstruction.
    internal const int MaxChatActions = 4;

    // Category is enum-constrained to the user's actual categories (like Receipt / CategorySuggestions)
    // so the model is forced to emit a real category name for add/edit drafts and ledger filters --
    // otherwise a free-string guess that doesn't match gets the whole action dropped in
    // AiAssistantService.IsActionSafe. Falls back to a plain string when the caller has no categories.
    // Field groups that only matter to one kind of request are opted into per turn rather than
    // carried on every call. This keeps schemas and model output focused on the current intent.
    public static object Chat(
        IReadOnlyList<string> categories,
        bool includeLedgerFilters = false,
        bool includeReminderControls = false) => Obj(
        new Dictionary<string, object>
        {
            ["reply"] = Str("Concise user-facing reply."),
            ["closeChat"] = Bool(),
            ["actions"] = Arr(Obj(
                new Dictionary<string, object>
                {
                    ["type"] = Str(enums:
                    [
                        "openLedger", "openDashboard", "openRecurring", "openWishlist", "openReports", "openInvestments",
                        "openAddLedgerDraft", "openAddRecurringDraft", "openAddWishlistDraft",
                        "openEditLedgerDraft", "openEditRecurringDraft", "openEditWishlistDraft",
                        "openAddSavingsGoalDraft", "openEditSavingsGoalDraft",
                        "requestDeleteLedger", "requestDeleteRecurring", "requestDeleteWishlist",
                        "requestConfirmRecurringBill", "requestDiscardRecurringBill",
                        "requestPurchaseWishlist", "requestUnpurchaseWishlist", "toggleRecurring",
                        ..(includeReminderControls ? new[] { "updateRecurringReminder" } : []),
                        "openLedgerExport"
                    ]),
                    ["payload"] = ActionPayload(categories, includeLedgerFilters, includeReminderControls)
                },
                ["type", "payload"]), maxItems: MaxChatActions)
        },
        ["reply", "closeChat", "actions"]);

    // Ledger creation uses a dedicated schema instead of the large generic ActionPayload union.
    // This keeps the structured-output contract focused while making every field needed to
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
                                ["ledgerCategory"] = Str("Income for inflows; Essentials for outflows unless explicitly requested otherwise.", enums: ["Essentials", "Growth", "Stability", "Rewards", "Income"]),
                                ["ledgerCategorySpecified"] = Bool(),
                                ["transferSource"] = Str(enums: ["Essentials", "Growth", "Stability", "Rewards"]),
                                ["transferTarget"] = Str(enums: ["Essentials", "Growth", "Stability", "Rewards"]),
                                ["accountId"] = Str("Exact active ledger account id from ledgerAccounts when explicitly named."),
                                ["counterAccountId"] = Str("Exact active destination ledger account id for an explicitly named transfer."),
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

    public static object ReportReviewChat() => ContextualChat(
        ["openReports"],
        new Dictionary<string, object>
        {
            ["cycleKey"] = Str("Validated selected cycle in YYYY-MM format.")
        });

    public static object InvestmentExplainChat() => ContextualChat(
        ["openInvestments"],
        new Dictionary<string, object>());

    public static object RewardsPlanChat() => ContextualChat(
        ["openWishlist", "openAddSavingsGoalDraft", "openEditSavingsGoalDraft"],
        new Dictionary<string, object>
        {
            ["id"] = Int(),
            ["savingsGoalId"] = Int(),
            ["name"] = Str("Savings Goal name.") ,
            ["targetAmount"] = Num("Positive target amount.", 0),
            ["targetDate"] = Str("Target date in YYYY-MM-DD format."),
            ["priority"] = Str(enums: ["low", "medium", "high"]),
            ["isRecurring"] = Bool(),
            ["recurrenceMonths"] = Int(),
            ["fundingBucket"] = Str("Savings goals may use Essentials or Rewards.", enums: ["Essentials", "Rewards"]),
            ["changes"] = Obj(new Dictionary<string, object>
            {
                ["name"] = Str(), ["targetAmount"] = Num(minimum: 0),
                ["targetDate"] = Str(), ["priority"] = Str(enums: ["low", "medium", "high"]),
                ["isRecurring"] = Bool(), ["recurrenceMonths"] = Int(),
                ["fundingBucket"] = Str("Savings goals may use Essentials or Rewards.", enums: ["Essentials", "Rewards"])
            })
        });

    private static object ContextualChat(
        IReadOnlyList<string> actionTypes,
        Dictionary<string, object> payloadProperties) => Obj(
        new Dictionary<string, object>
        {
            ["reply"] = Str("Concise beginner-friendly explanation using only server-provided evidence."),
            ["closeChat"] = Bool(),
            ["actions"] = Arr(Obj(
                new Dictionary<string, object>
                {
                    ["type"] = Str(enums: actionTypes),
                    ["payload"] = Obj(payloadProperties)
                },
                ["type", "payload"]),
                maxItems: 2)
        },
        ["reply", "closeChat", "actions"]);

    private static readonly IReadOnlyList<string> IntentEnum = AiAssistantService.KnownIntentNames;

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
                    ["ledgerAccountReference"] = NullableString("Named ledger account the user referenced; null otherwise."),
                    ["category"] = NullableString("Category name the user referenced; null otherwise."),
                    ["ledgerCategory"] = NullableString("Ledger category; null otherwise."),
                    ["date"] = NullableString("Explicit date in YYYY-MM-DD format; null otherwise."),
                    ["amount"] = NullableNumber("Non-negative amount mentioned by the user; null otherwise.")
                },
                ["searchText", "cycleHint", "date", "wishlistReference", "transactionReference", "ledgerAccountReference", "category", "ledgerCategory", "amount"]),
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
                    ["type"] = Str(enums: ["delete", "merge", "add", "consolidate", "changeFlow"]),
                    ["title"] = Str("Short title."),
                    ["summary"] = Str("One-sentence reason."),
                    ["categories"] = Arr(Str("Existing category name.")),
                    ["targetCategory"] = NullableString("Existing destination category for merge; null otherwise."),
                    ["newCategoryName"] = NullableString("New category for add; null otherwise."),
                    ["sourceFlow"] = NullableString("Current flow for changeFlow; null otherwise."),
                    ["targetFlow"] = NullableString("New both, inflow, or outflow value for changeFlow; null otherwise."),
                    ["confidence"] = Num("Confidence from 0 to 1.", 0, 1)
                },
                ["type", "title", "summary", "categories", "targetCategory", "newCategoryName", "sourceFlow", "targetFlow", "confidence"]),
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

    public static object ReceiptSplit(IReadOnlyList<string> categories) => Obj(
        new Dictionary<string, object>
        {
            ["description"] = Str("Merchant name or short purchase description."),
            ["date"] = NullableString("Visible receipt date in YYYY-MM-DD format; null if unclear."),
            ["currency"] = NullableString("Visible ISO 4217 currency code or currency symbol; null if unclear."),
            ["subtotal"] = NullableNumber("Printed subtotal before receipt-level charges; null if absent or unclear."),
            ["total"] = NullableNumber("Printed final total; null if absent or unclear."),
            ["category"] = Str(enums: categories),
            ["ledgerCategory"] = Str(enums: ["Essentials", "Growth", "Stability", "Rewards"]),
            ["items"] = Arr(Obj(
                new Dictionary<string, object>
                {
                    ["name"] = Str("Receipt line description."),
                    ["quantity"] = Int(),
                    ["unitPrice"] = NullableNumber("Printed or directly derivable unit price; null if unclear."),
                    ["lineTotal"] = NullableNumber("Printed line total before receipt-level charges; null if unclear."),
                    ["confidence"] = Num("Line extraction confidence from 0 to 1.", 0, 1)
                },
                ["name", "quantity", "unitPrice", "lineTotal", "confidence"])),
            ["charges"] = Arr(Obj(
                new Dictionary<string, object>
                {
                    ["label"] = Str("Printed charge or discount label."),
                    ["kind"] = Str(enums: ["tax", "service", "tip", "discount", "rounding", "other"]),
                    ["operation"] = Str(enums: ["add", "subtract", "included"]),
                    ["basis"] = Str(enums: ["subtotal", "runningTotal"]),
                    ["amount"] = NullableNumber("Printed absolute charge amount; null when only a rate is visible."),
                    ["ratePercent"] = NullableNumber("Printed percentage such as 10 for 10%; null when absent."),
                    ["sequence"] = Int(),
                    ["eligibleItemIndexes"] = Arr(Int()),
                    ["confidence"] = Num("Charge extraction confidence from 0 to 1.", 0, 1)
                },
                ["label", "kind", "operation", "basis", "amount", "ratePercent",
                    "sequence", "eligibleItemIndexes", "confidence"])),
            ["fieldConfidence"] = Obj(
                new Dictionary<string, object>
                {
                    ["description"] = Num("Merchant confidence from 0 to 1.", 0, 1),
                    ["date"] = Num("Date confidence from 0 to 1.", 0, 1),
                    ["currency"] = Num("Currency confidence from 0 to 1.", 0, 1),
                    ["subtotal"] = Num("Subtotal confidence from 0 to 1.", 0, 1),
                    ["total"] = Num("Final total confidence from 0 to 1.", 0, 1)
                },
                ["description", "date", "currency", "subtotal", "total"]),
            ["truncated"] = Bool(),
            ["warnings"] = Arr(Str("Short review warning about unclear receipt content.")),
            ["confidence"] = Num("Overall extraction confidence from 0 to 1.", 0, 1)
        },
        ["description", "date", "currency", "subtotal", "total", "category", "ledgerCategory",
            "items", "charges", "fieldConfidence", "truncated", "warnings", "confidence"]);

    public static readonly object InvestmentActivityScan = Obj(
        new Dictionary<string, object>
        {
            ["type"] = NullableString("Buy, Sell, Dividend, FeeTax, Deposit, Withdrawal, or Conversion; null if unclear."),
            ["accountId"] = NullableString("Exact available account id; null if unclear."),
            ["instrumentId"] = NullableString("Exact available investment id; null if unclear."),
            ["tradeDate"] = NullableString("Visible activity date in YYYY-MM-DD; null if unclear."),
            ["units"] = NullableNumber("Positive units; null if unclear or not applicable."),
            ["unitPrice"] = NullableNumber("Positive price per unit; null if unclear or not applicable."),
            ["cashAmount"] = NullableNumber("Positive gross amount, dividend, charge, deposit, withdrawal, or source conversion amount; null if unclear."),
            ["fees"] = NullableNumber("Non-negative fee amount; null if not explicitly supported."),
            ["taxes"] = NullableNumber("Non-negative tax amount; null if not explicitly supported."),
            ["currency"] = NullableString("Visible source currency code; null if unclear or not applicable."),
            ["toCurrency"] = NullableString("Visible destination currency code for a conversion; null otherwise."),
            ["toAmount"] = NullableNumber("Positive destination amount for a conversion; null otherwise."),
            ["confidence"] = Num("Overall extraction confidence from 0 to 1.", 0, 1)
        },
        ["type", "accountId", "instrumentId", "tradeDate", "units", "unitPrice",
            "cashAmount", "fees", "taxes", "currency", "toCurrency", "toAmount", "confidence"]);

    private static object ActionPayload(
        IReadOnlyList<string> categories,
        bool includeLedgerFilters,
        bool includeReminderControls)
    {
        var properties = BaseActionPayloadProperties(categories);
        if (includeLedgerFilters)
        {
            // Ledger filter-bar fields. These mirror the app's advanced filter panel; without them
            // in the schema the model physically cannot express "show purchases over 200" and the
            // filter silently does nothing.
            properties["minAmount"] = Num();
            properties["maxAmount"] = Num();
            properties["recurringOnly"] = Bool();
            properties["wishlistOnly"] = Bool();
        }
        if (includeReminderControls)
        {
            // Per-subscription push reminder settings (updateRecurringReminder).
            properties["enabled"] = Bool();
            properties["reminderMode"] = Str(enums: ["Once", "Daily"]);
            properties["leadDays"] = Int();
        }
        return Obj(properties);
    }

    private static Dictionary<string, object> BaseActionPayloadProperties(IReadOnlyList<string> categories) => new()
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
        ["accountId"] = Str("Exact active ledger account id from ledgerAccounts when explicitly named."),
        ["counterAccountId"] = Str("Exact active destination ledger account id for an explicitly named transfer."),
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
    };

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
