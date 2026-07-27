using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private async Task<AiChatResponse> EnrichLedgerDraftActionsAsync(
        AiChatResponse response,
        string userMessage,
        AiContext context,
        CancellationToken cancellationToken)
    {
        if (!response.Actions.Any(action =>
                action.Type.Equals("openAddLedgerDraft", StringComparison.OrdinalIgnoreCase)))
        {
            return response;
        }

        var sourceLines = userMessage
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        var draftCount = response.Actions.Count(action =>
            action.Type.Equals("openAddLedgerDraft", StringComparison.OrdinalIgnoreCase));
        var normalCategories = context.Categories
            .Where(category => !TransactionCategoryService.IsReservedName(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var enriched = new List<AiUiAction>(response.Actions.Count);
        var draftIndex = 0;
        foreach (var action in response.Actions)
        {
            if (!action.Type.Equals("openAddLedgerDraft", StringComparison.OrdinalIgnoreCase))
            {
                enriched.Add(action);
                continue;
            }

            var payload = new Dictionary<string, object?>(action.Payload, StringComparer.OrdinalIgnoreCase);
            // Only a one-line-per-draft message lets us attribute a keyword to a single
            // draft. Otherwise a "growth" anywhere in the message would tag every draft.
            var perDraftSource = sourceLines.Count == draftCount;
            var sourceText = perDraftSource ? sourceLines[draftIndex] : userMessage;
            draftIndex++;

            var txType = ReadPayloadString(payload, "txType") ?? "outflow";
            if (!txType.Equals("transfer", StringComparison.OrdinalIgnoreCase))
            {
                if (perDraftSource) ApplyExplicitLedgerCategory(sourceText, payload, txType);
                await ApplyBestNormalCategoryAsync(
                    sourceText,
                    txType,
                    normalCategories,
                    payload,
                    cancellationToken);
            }

            enriched.Add(action with { Payload = payload });
        }

        return response with { Actions = enriched };
    }

    private static void ApplyExplicitLedgerCategory(string sourceText, Dictionary<string, object?> payload, string txType)
    {
        var isInflow = txType.Equals("inflow", StringComparison.OrdinalIgnoreCase);
        string? ledger = null;
        if (isInflow && Regex.IsMatch(sourceText, @"\bincome\b", RegexOptions.IgnoreCase)) ledger = "Income";
        else if (Regex.IsMatch(sourceText, @"\brewards?\b", RegexOptions.IgnoreCase)) ledger = "Rewards";
        else if (Regex.IsMatch(sourceText, @"\bgrowth\b", RegexOptions.IgnoreCase)) ledger = "Growth";
        else if (Regex.IsMatch(sourceText, @"\bstability\b", RegexOptions.IgnoreCase)) ledger = "Stability";
        else if (Regex.IsMatch(sourceText, @"\bessentials?\b", RegexOptions.IgnoreCase)) ledger = "Essentials";

        if (ledger == null)
        {
            // No keyword in this line: keep whatever the model chose rather than
            // downgrading a deliberate pick back to Essentials.
            if (ReadPayloadString(payload, "ledgerCategory") is { Length: > 0 }) return;
            payload["ledgerCategory"] = isInflow ? "Income" : "Essentials";
            payload["ledgerCategorySpecified"] = false;
            return;
        }

        payload["ledgerCategory"] = ledger;
        payload["ledgerCategorySpecified"] = true;
    }

    private async Task ApplyBestNormalCategoryAsync(
        string sourceText,
        string txType,
        IReadOnlyList<string> normalCategories,
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var explicitCategory = normalCategories.FirstOrDefault(category => ContainsNamedValue(sourceText, category));
        if (explicitCategory != null)
        {
            payload["category"] = explicitCategory;
            return;
        }

        if (_categorySuggestionService == null) return;
        var description = ReadPayloadString(payload, "description");
        if (string.IsNullOrWhiteSpace(description)) return;

        var result = await _categorySuggestionService.SuggestAsync(
            description,
            txType,
            normalCategories,
            cancellationToken);
        var suggested = result.Data?.FirstOrDefault()?.Category;
        var canonical = normalCategories.FirstOrDefault(category =>
            category.Equals(suggested, StringComparison.OrdinalIgnoreCase));
        if (canonical != null) payload["category"] = canonical;
    }

    private static bool ContainsNamedValue(string sourceText, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        return Regex.IsMatch(
            sourceText,
            $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(candidate.Trim())}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase);
    }
}
