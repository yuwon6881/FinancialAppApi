using FinancialAppApi.Models;
using FinancialAppApi.Services.Accounts;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    internal sealed record AiLedgerAccountRow(
        string Id,
        string Name,
        string Bucket,
        string Kind,
        bool IsArchived,
        decimal Balance);

    internal sealed record AiLedgerAccountContext(
        IReadOnlyList<AiLedgerAccountRow> Accounts,
        string? SelectedAccountId,
        string? SelectionIssue,
        object? Activity,
        object? Payload);

    private sealed record AccountActivityRow(
        string Id,
        DateTime Date,
        string Category,
        string LedgerCategory,
        decimal Amount,
        string? AccountId,
        string? CounterAccountId);

    private async Task<AiLedgerAccountContext> LoadLedgerAccountContextAsync(
        AiIntentPlan intentPlan,
        TargetCycleSelection targetSelection,
        int cycleDay,
        bool sensitiveMode,
        CancellationToken cancellationToken)
    {
        if (!intentPlan.QueryPlan.NeedsLedgerAccounts || sensitiveMode)
            return new([], null, null, null, null);

        var accounts = await _context.LedgerAccounts
            .AsNoTracking()
            .OrderBy(account => account.Bucket)
            .ThenBy(account => account.Name)
            .ThenBy(account => account.Id)
            .ToListAsync(cancellationToken);
        var accountsById = accounts.ToDictionary(account => account.Id, StringComparer.Ordinal);
        var reference = intentPlan.Entities?.LedgerAccountReference;
        var selected = ResolveAccount(reference, accounts);
        var selectionIssue = reference is null || selected != null
            ? null
            : $"I couldn't find a ledger account named \"{reference}\". Please use the exact account name shown in your Ledger accounts.";
        var ambiguous = reference is not null && selected == null && accounts.Count(account =>
            account.Name.Contains(reference, StringComparison.OrdinalIgnoreCase)) > 1;
        if (ambiguous)
        {
            selectionIssue = $"I found more than one ledger account matching \"{reference}\". Please use the exact account name.";
        }

        var balances = await _ledgerAccountService.GetBalancesAsync(accounts, cancellationToken);
        var rows = accounts
            .Select(account => new AiLedgerAccountRow(
                account.Id,
                account.Name,
                account.Bucket,
                account.Kind,
                account.IsArchived,
                balances.GetValueOrDefault(account.Id)))
            .ToList();
        var activity = WantsAccountActivity(intentPlan.QueryPlan.QueryText)
            ? await LoadAccountActivityAsync(accounts, accountsById, selected?.Id, targetSelection.Cycles, cycleDay, cancellationToken)
            : null;
        var payload = new
        {
            note = "Ledger account balances are derived from the complete ledger history. Account activity is scoped to requestedCycles.",
            accounts = rows.Select(row => new
            {
                accountId = row.Id,
                name = row.Name,
                bucket = row.Bucket,
                kind = row.Kind,
                isArchived = row.IsArchived,
                balance = row.Balance
            }).ToList(),
            selectedAccountId = selected?.Id,
            accountActivity = activity
        };
        return new AiLedgerAccountContext(rows, selected?.Id, selectionIssue, activity, payload);
    }

    private static LedgerAccount? ResolveAccount(string? reference, IReadOnlyList<LedgerAccount> accounts)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var exactId = accounts.FirstOrDefault(account =>
            account.Id.Equals(reference.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exactId != null) return exactId;
        var exactNames = accounts.Where(account =>
            account.Name.Equals(reference.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (exactNames.Count == 1) return exactNames[0];
        if (exactNames.Count > 1) return null;
        var partial = accounts.Where(account =>
            account.Name.Contains(reference.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }

    private static bool WantsAccountActivity(string queryText) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            queryText,
            @"\b(spend|spent|spending|activity|transaction|transactions|purchase|purchases|paid|payment|payments|inflow|outflow|deposit|debit|credit|history|how much did)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private async Task<object?> LoadAccountActivityAsync(
        IReadOnlyList<LedgerAccount> accounts,
        IReadOnlyDictionary<string, LedgerAccount> accountsById,
        string? selectedAccountId,
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        CancellationToken cancellationToken)
    {
        if (cycles.Count == 0) return null;
        var ranges = MergeCycleRanges(cycles, cycleDay);
        var rows = new List<AccountActivityRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var range in ranges)
        {
            var rangeRows = await _context.Transactions
                .AsNoTracking()
                .Where(transaction => transaction.LedgerCategory != "Discarded" &&
                    transaction.Date >= range.Start && transaction.Date < range.End)
                .Select(transaction => new AccountActivityRow(
                    transaction.Id,
                    transaction.Date,
                    transaction.Category,
                    transaction.LedgerCategory,
                    transaction.Amount,
                    transaction.AccountId,
                    transaction.CounterAccountId))
                .ToListAsync(cancellationToken);
            foreach (var row in rangeRows)
            {
                if (seen.Add(row.Id)) rows.Add(row);
            }
        }

        var selectedAccounts = selectedAccountId is { } selected && accountsById.ContainsKey(selected)
            ? accounts.Where(account => account.Id == selected).ToList()
            : accounts.ToList();
        var activityRows = selectedAccounts.Select(account =>
        {
            var net = 0m;
            var inflow = 0m;
            var outflow = 0m;
            var count = 0;
            foreach (var row in rows)
            {
                var transaction = new Transaction
                {
                    Amount = row.Amount,
                    Category = row.Category,
                    LedgerCategory = row.LedgerCategory,
                    AccountId = row.AccountId,
                    CounterAccountId = row.CounterAccountId
                };
                var amount = LedgerAccountAttribution.GetAccountAmount(transaction, account, accountsById);
                if (amount == 0m) continue;
                net += amount;
                if (amount > 0m) inflow += amount;
                else outflow += Math.Abs(amount);
                count++;
            }
            return new
            {
                accountId = account.Id,
                name = account.Name,
                bucket = account.Bucket,
                transactionCount = count,
                inflow,
                outflow,
                netChange = net
            };
        }).ToList();
        return new
        {
            cycles = cycles.Select(FormatCycleKey).ToList(),
            accounts = activityRows
        };
    }
}
