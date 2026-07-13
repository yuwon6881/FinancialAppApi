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
            var sourceText = sourceLines.Count == draftCount ? sourceLines[draftIndex] : userMessage;
            draftIndex++;

            var txType = ReadPayloadString(payload, "txType") ?? "outflow";
            if (!txType.Equals("transfer", StringComparison.OrdinalIgnoreCase))
            {
                ApplyExplicitLedgerCategory(sourceText, payload);
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

    private static void ApplyExplicitLedgerCategory(string sourceText, Dictionary<string, object?> payload)
    {
        string? ledger = null;
        if (Regex.IsMatch(sourceText, @"\brewards?\b", RegexOptions.IgnoreCase)) ledger = "Rewards";
        else if (Regex.IsMatch(sourceText, @"\bgrowth\b", RegexOptions.IgnoreCase)) ledger = "Growth";
        else if (Regex.IsMatch(sourceText, @"\bstability\b", RegexOptions.IgnoreCase)) ledger = "Stability";
        else if (Regex.IsMatch(sourceText, @"\bessentials?\b", RegexOptions.IgnoreCase)) ledger = "Essentials";

        if (ledger == null)
        {
            payload["ledgerCategory"] = "Essentials";
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
