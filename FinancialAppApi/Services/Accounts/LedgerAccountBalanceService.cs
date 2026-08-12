using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Accounts;

public sealed record LedgerAccountBalance(string AccountId, decimal Remaining);

/// <summary>
/// Reads account balances from the complete ledger history. Accounts are a projection over the
/// four existing bucket totals, so this service owns no cache and never participates in cycle
/// balance arithmetic.
/// </summary>
public sealed class LedgerAccountBalanceService
{
    private readonly AppDbContext _context;

    public LedgerAccountBalanceService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetBalancesAsync(
        IReadOnlyCollection<LedgerAccount>? accounts = null,
        CancellationToken cancellationToken = default,
        DateTime? throughExclusive = null)
    {
        var accountRows = accounts is null
            ? await _context.LedgerAccounts.AsNoTracking().ToListAsync(cancellationToken)
            : accounts.ToList();
        var accountsById = accountRows.ToDictionary(account => account.Id, StringComparer.Ordinal);
        var defaults = accountRows
            .Where(account => account.IsDefault && !account.IsArchived)
            .GroupBy(account => account.Bucket, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);
        var balances = accountRows.ToDictionary(account => account.Id, _ => 0m, StringComparer.Ordinal);

        if (accountRows.Count == 0) return balances;

        var transactionQuery = _context.Transactions
            .AsNoTracking()
            .AsQueryable();
        if (throughExclusive.HasValue)
        {
            transactionQuery = transactionQuery.Where(transaction => transaction.Date < throughExclusive.Value);
        }

        var transactions = await transactionQuery
            .Select(transaction => new LedgerTransactionProjection(
                transaction.Category,
                transaction.LedgerCategory,
                transaction.Amount,
                transaction.AccountId,
                transaction.CounterAccountId))
            .ToListAsync(cancellationToken);

        foreach (var projection in transactions)
        {
            var transaction = projection.ToTransaction();
            foreach (var account in accountRows)
            {
                var amount = LedgerAccountAttribution.GetAccountAmount(
                    transaction,
                    account,
                    accountsById,
                    defaults);
                if (amount != 0m) balances[account.Id] += amount;
            }
        }

        return balances;
    }

    private sealed record LedgerTransactionProjection(
        string Category,
        string LedgerCategory,
        decimal Amount,
        string? AccountId,
        string? CounterAccountId)
    {
        public Transaction ToTransaction() => new()
        {
            Category = Category,
            LedgerCategory = LedgerCategory,
            Amount = Amount,
            AccountId = AccountId,
            CounterAccountId = CounterAccountId,
        };
    }
}
