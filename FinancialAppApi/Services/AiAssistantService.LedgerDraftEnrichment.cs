using System.Text.RegularExpressions;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private async Task<AiChatResponse> EnrichLedgerDraftActionsAsync(
        AiChatResponse response,
        string userMessage,
        AiContext context,
        IReadOnlyList<AiResolvedAccountMention> accountMentions,
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
                ApplyBestNormalCategory(sourceText, normalCategories, payload);
            }
            if (perDraftSource) ApplyExplicitLedgerAccount(sourceText, payload, context);
            // A mention is the user pointing at one exact account, so it outranks any name the
            // model matched out of the prose -- and it is the only thing that can place both
            // sides of a move between two accounts in one bucket.
            if (draftCount == 1) ApplyMentionedLedgerAccounts(payload, accountMentions);

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

    private static void ApplyBestNormalCategory(
        string sourceText,
        IReadOnlyList<string> normalCategories,
        Dictionary<string, object?> payload)
    {
        var explicitCategory = normalCategories.FirstOrDefault(category => ContainsNamedValue(sourceText, category));
        if (explicitCategory != null)
        {
            payload["category"] = explicitCategory;
            return;
        }

        // The structured chat schema already restricts category to this canonical list. Do not
        // start another provider request per draft merely to second-guess a valid reviewed value.
        // An explicit category in the user's line still wins deterministically above.
        var current = ReadPayloadString(payload, "category");
        var canonical = normalCategories.FirstOrDefault(category =>
            category.Equals(current, StringComparison.OrdinalIgnoreCase));
        if (canonical != null) payload["category"] = canonical;
    }

    private static void ApplyExplicitLedgerAccount(
        string sourceText,
        Dictionary<string, object?> payload,
        AiContext context)
    {
        var accountRows = context.LedgerAccountContext?.Accounts
            .Where(account => !account.IsArchived)
            .ToList();
        if (accountRows is not { Count: > 0 }) return;
        var named = accountRows
            .Where(account => ContainsNamedValue(sourceText, account.Name))
            .ToList();
        if (named.Count == 0) return;

        var txType = ReadPayloadString(payload, "txType") ?? "outflow";
        if (txType.Equals("transfer", StringComparison.OrdinalIgnoreCase))
        {
            var source = ReadPayloadString(payload, "transferSource");
            var target = ReadPayloadString(payload, "transferTarget");
            var sourceAccount = named.FirstOrDefault(account => string.Equals(account.Bucket, source, StringComparison.OrdinalIgnoreCase));
            var targetAccount = named.FirstOrDefault(account => string.Equals(account.Bucket, target, StringComparison.OrdinalIgnoreCase));
            if (sourceAccount != null) payload["accountId"] = sourceAccount.Id;
            if (targetAccount != null) payload["counterAccountId"] = targetAccount.Id;
            return;
        }

        var ledger = ReadPayloadString(payload, "ledgerCategory");
        var matching = named.Where(account => string.Equals(account.Bucket, ledger, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count == 1 && !string.Equals(ledger, "Income", StringComparison.OrdinalIgnoreCase))
            payload["accountId"] = matching[0].Id;
    }

    // Mentions are ordered by where they appear in the message, which is what makes "from @cimb
    // to @ryt" directional. One mention places the row itself; two place a movement's endpoints.
    private static void ApplyMentionedLedgerAccounts(
        Dictionary<string, object?> payload,
        IReadOnlyList<AiResolvedAccountMention> mentions)
    {
        if (mentions.Count == 0) return;
        var txType = ReadPayloadString(payload, "txType") ?? "outflow";
        var isTransfer = txType.Equals("transfer", StringComparison.OrdinalIgnoreCase);

        if (isTransfer && mentions.Count >= 2)
        {
            payload["accountId"] = mentions[0].AccountId;
            payload["counterAccountId"] = mentions[1].AccountId;
            payload["transferSource"] = mentions[0].Bucket;
            payload["transferTarget"] = mentions[1].Bucket;
            return;
        }

        var placement = mentions[0];
        if (isTransfer)
        {
            // Only one side was named. Place that side and leave the other to the reviewed
            // editor rather than inventing the account the money is going to.
            var source = ReadPayloadString(payload, "transferSource");
            if (string.Equals(placement.Bucket, source, StringComparison.OrdinalIgnoreCase))
            {
                payload["accountId"] = placement.AccountId;
            }
            else if (string.Equals(placement.Bucket, ReadPayloadString(payload, "transferTarget"), StringComparison.OrdinalIgnoreCase))
            {
                payload["counterAccountId"] = placement.AccountId;
            }
            return;
        }

        // An Income parent carries no account of its own -- its generated children do.
        var ledger = ReadPayloadString(payload, "ledgerCategory");
        if (string.Equals(ledger, "Income", StringComparison.OrdinalIgnoreCase)) return;
        payload["accountId"] = placement.AccountId;
        // The named account decides the bucket: picking "@CIMB Savings" and being filed under a
        // bucket that account does not belong to is the placement failure this feature exists to
        // remove, and the safety pass would drop the id rather than the bucket.
        payload["ledgerCategory"] = placement.Bucket;
        payload["ledgerCategorySpecified"] = true;
    }

    // Moving money between two accounts is a complete instruction on its own -- the description
    // field is the app's, not the user's, so a transfer must never be refused or held up for one.
    // Applied during normalization, before the record is checked for a description it should never
    // have had to carry.
    private static void ApplyDefaultTransferDescription(Dictionary<string, object?> payload, AiContext context)
    {
        var txType = ReadPayloadString(payload, "txType") ?? "outflow";
        if (!txType.Equals("transfer", StringComparison.OrdinalIgnoreCase)) return;
        if (ReadPayloadString(payload, "description") is { Length: > 0 }) return;

        string? NameOf(string key)
        {
            var id = ReadPayloadString(payload, key);
            return id == null
                ? null
                : context.LedgerAccountContext?.Accounts
                    .FirstOrDefault(account => account.Id.Equals(id, StringComparison.Ordinal))?.Name;
        }

        var from = NameOf("accountId") ?? ReadPayloadString(payload, "transferSource");
        var to = NameOf("counterAccountId") ?? ReadPayloadString(payload, "transferTarget");
        payload["description"] = from != null && to != null
            ? $"Transfer {from} to {to}"
            : "Transfer";
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
