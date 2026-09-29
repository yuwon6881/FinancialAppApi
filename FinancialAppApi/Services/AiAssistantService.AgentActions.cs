using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FinancialAppApi.Services.AI.Agent;
using FinancialAppApi.Services.AI.Tools;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

// Action validation for the tool-calling engine. Proposed actions go through the same rules as
// the legacy engine (ValidateActionAsync); the difference is where known records come from.
// Instead of preloaded context, the validation context holds only records a tool showed the
// model in this turn (or that the previous turn matched), re-read from the database -- so an id
// must be both surfaced as evidence and still exist for this user.
public partial class AiAssistantService
{
    private async Task<AiContext> BuildAgentValidationContextAsync(
        AiToolContext toolContext,
        IReadOnlyList<string> carriedTransactionIds,
        CancellationToken cancellationToken)
    {
        var categories = (await _categoryService.GetCategoriesAsync())
            .Where(category => !TransactionCategoryService.IsReservedName(category.Name))
            .Select(category => category.Name)
            .OrderBy(name => name)
            .ToList();

        var transactionIds = toolContext.Evidence.IdsOf(AiEvidenceLedger.Transaction)
            .Concat(carriedTransactionIds)
            .Distinct(StringComparer.Ordinal)
            .Take(200)
            .ToList();
        var transactions = transactionIds.Count == 0
            ? []
            : (await _context.Transactions
                    .AsNoTracking()
                    .Where(t => transactionIds.Contains(t.Id) && t.LedgerCategory != "Discarded")
                    .Select(t => new AiTransactionDbRow(t.Id, t.Date, t.PostedAt, t.Description, t.Category, t.LedgerCategory, t.Amount, t.RecurringPaymentId, t.AccountId, t.CounterAccountId))
                    .ToListAsync(cancellationToken))
                .Select(ToAiTransactionRow)
                .ToList();

        var recurringIds = toolContext.Evidence.IdsOf(AiEvidenceLedger.Recurring).ToHashSet(StringComparer.Ordinal);
        var recurring = recurringIds.Count == 0
            ? []
            : (await LoadRecurringRowsAsync(cancellationToken)).Where(row => recurringIds.Contains(row.Id)).ToList();
        var wishlistIds = toolContext.Evidence.IdsOf(AiEvidenceLedger.Wishlist).ToHashSet(StringComparer.Ordinal);
        var wishlist = wishlistIds.Count == 0
            ? []
            : (await LoadWishlistRowsAsync(null, cancellationToken))
                .Where(row => wishlistIds.Contains(row.Id.ToString(CultureInfo.InvariantCulture)))
                .ToList();
        // Account placement checks existence, bucket, and archived state only; balances are not needed.
        var accounts = (await _ledgerAccountService.GetAccountsAsync(cancellationToken))
            .Select(account => new AiLedgerAccountRow(account.Id, account.Name, account.Bucket, account.Kind, account.IsArchived))
            .ToList();

        return new AiContext(
            toolContext.SensitiveMode,
            categories,
            LedgerCategories,
            transactions,
            recurring,
            wishlist,
            new AiLedgerAccountContext(accounts));
    }

    private sealed class AgentActionProposer : IAiActionProposer
    {
        private readonly AiAssistantService _owner;
        private readonly AiToolContext _toolContext;
        private readonly string _userMessage;
        private readonly AiConstraints _constraints;
        private readonly IReadOnlyList<string> _carriedTransactionIds;
        // A shorthand list pins the draft count: "Coffee 12, Grab 8" is exactly two records.
        private readonly int _expectedDraftCount;
        private readonly ActionValidationState _state = new();
        private readonly List<AiUiAction> _accepted = [];

        public AgentActionProposer(
            AiAssistantService owner,
            AiToolContext toolContext,
            string userMessage,
            AiConstraints constraints,
            IReadOnlyList<string> carriedTransactionIds,
            IReadOnlyList<string> categories,
            int expectedDraftCount)
        {
            _owner = owner;
            _toolContext = toolContext;
            _userMessage = userMessage;
            _constraints = constraints;
            _carriedTransactionIds = carriedTransactionIds;
            _expectedDraftCount = expectedDraftCount;
            var chatSchema = JsonSerializer.SerializeToNode(AiResponseSchemas.Chat(categories, includeLedgerFilters: true, includeReminderControls: true))!;
            ParametersSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["actions"] = chatSchema["properties"]!["actions"]!.DeepClone() },
                ["required"] = new JsonArray("actions")
            };
        }

        public JsonObject ParametersSchema { get; }

        public IReadOnlyList<AiUiAction> Accepted => _accepted;

        // Last validation context, kept so enrichment after the loop sees the same records.
        public AiContext? ValidationContext { get; private set; }

        public async Task<string> ProposeAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            JsonElement actions;
            try
            {
                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                if (!document.RootElement.TryGetProperty("actions", out var array) || array.ValueKind != JsonValueKind.Array)
                    return Verdict([], "Arguments must be {\"actions\": [...]}.");
                actions = array.Clone();
            }
            catch (JsonException)
            {
                return Verdict([], "Arguments were not valid JSON.");
            }

            var proposed = actions.EnumerateArray().ToList();
            var drafts = proposed.Count(item =>
                item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String && type.GetString() == "openAddLedgerDraft");
            if (_expectedDraftCount > 0 && drafts > 0 && drafts + _accepted.Count(a => a.Type == "openAddLedgerDraft") != _expectedDraftCount)
                return Verdict([], $"The user listed exactly {_expectedDraftCount} record(s); stage exactly that many openAddLedgerDraft actions, one per record, in order.");

            await _toolContext.RefreshSensitiveModeAsync(cancellationToken);
            ValidationContext = await _owner.BuildAgentValidationContextAsync(_toolContext, _carriedTransactionIds, cancellationToken);
            var results = new List<JsonObject>();
            foreach (var (item, index) in proposed.Select((item, index) => (item, index)))
            {
                var type = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var typeElement) ? typeElement.ToString() : "";
                string? rejection;
                if (_accepted.Count >= AiResponseSchemas.MaxChatActions)
                {
                    rejection = $"At most {AiResponseSchemas.MaxChatActions} actions can be proposed per message.";
                }
                else
                {
                    var (action, reason) = await _owner.ValidateActionAsync(
                        item, ValidationContext, _userMessage, _constraints, _state, cancellationToken);
                    rejection = reason;
                    if (action != null) _accepted.Add(action);
                }
                var result = new JsonObject { ["index"] = index, ["type"] = type, ["status"] = rejection == null ? "accepted" : "rejected" };
                if (rejection != null) result["reason"] = rejection;
                results.Add(result);
            }
            return Verdict(results, null);
        }

        private static string Verdict(IReadOnlyList<JsonObject> results, string? error)
        {
            var verdict = new JsonObject
            {
                ["results"] = new JsonArray(results.Select(result => (JsonNode)result).ToArray()),
                ["note"] = "Accepted actions are shown to the user to review and confirm. Nothing is saved yet; do not say it was."
            };
            if (error != null) verdict["error"] = error;
            return verdict.ToJsonString();
        }
    }
}
