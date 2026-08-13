using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Accounts;

public sealed record LedgerAccountBalance(string AccountId, decimal Remaining);
public sealed record LedgerAccountBalanceSnapshot(
    IReadOnlyDictionary<string, decimal> Current,
    IReadOnlyDictionary<string, decimal> ThroughExclusive);

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
        if (accountRows.Count == 0) return EmptyBalances(accountRows);

        var transactionQuery = _context.Transactions
            .AsNoTracking()
            .AsQueryable();
        if (throughExclusive.HasValue)
        {
            transactionQuery = transactionQuery.Where(transaction => transaction.Date < throughExclusive.Value);
        }

        var transactions = await transactionQuery
            .Select(transaction => new LedgerTransactionProjection(
                transaction.Date,
                transaction.Category,
                transaction.LedgerCategory,
                transaction.Amount,
                transaction.AccountId,
                transaction.CounterAccountId))
            .ToListAsync(cancellationToken);
        return Calculate(accountRows, accountsById, defaults, transactions);
    }

    public async Task<LedgerAccountBalanceSnapshot> GetBalanceSnapshotAsync(
        IReadOnlyCollection<LedgerAccount> accounts,
        DateTime throughExclusive,
        CancellationToken cancellationToken = default)
    {
        var accountRows = accounts.ToList();
        if (accountRows.Count == 0)
        {
            var empty = EmptyBalances(accountRows);
            return new LedgerAccountBalanceSnapshot(empty, empty);
        }

        var transactions = await _context.Transactions
            .AsNoTracking()
            .Select(transaction => new LedgerTransactionProjection(
                transaction.Date,
                transaction.Category,
                transaction.LedgerCategory,
                transaction.Amount,
                transaction.AccountId,
                transaction.CounterAccountId))
            .ToListAsync(cancellationToken);
        var accountsById = accountRows.ToDictionary(account => account.Id, StringComparer.Ordinal);
        var defaults = DefaultAccounts(accountRows);
        var current = EmptyBalances(accountRows);
        var through = EmptyBalances(accountRows);
        foreach (var transaction in transactions)
        {
            Accumulate(current, transaction.ToTransaction(), accountsById, defaults);
            if (transaction.Date < throughExclusive)
                Accumulate(through, transaction.ToTransaction(), accountsById, defaults);
        }
        return new LedgerAccountBalanceSnapshot(current, through);
    }

    private static IReadOnlyDictionary<string, decimal> Calculate(
        IReadOnlyCollection<LedgerAccount> accounts,
        IReadOnlyDictionary<string, LedgerAccount> accountsById,
        IReadOnlyDictionary<string, string> defaults,
        IEnumerable<LedgerTransactionProjection> transactions)
    {
        var balances = EmptyBalances(accounts);
        foreach (var projection in transactions)
        {
            Accumulate(balances, projection.ToTransaction(), accountsById, defaults);
        }
        return balances;
    }

    private static void Accumulate(
        IDictionary<string, decimal> balances,
        Transaction transaction,
        IReadOnlyDictionary<string, LedgerAccount> accountsById,
        IReadOnlyDictionary<string, string> defaults)
    {
        if (string.Equals(transaction.LedgerCategory, "AccountMove", StringComparison.OrdinalIgnoreCase))
        {
            Add(balances, transaction.AccountId, -Math.Abs(transaction.Amount));
            Add(balances, transaction.CounterAccountId, Math.Abs(transaction.Amount));
            return;
        }

        foreach (var bucket in LedgerBuckets)
        {
            var leg = CategoryAttributionService.GetCategoryAmount(transaction, bucket);
            if (leg == 0m) continue;
            Add(balances, LedgerAccountAttribution.GetPlacementAccountId(transaction, bucket, accountsById, defaults), leg);
        }
    }

    private static readonly string[] LedgerBuckets = ["Essentials", "Growth", "Stability", "Rewards"];

    private static Dictionary<string, decimal> EmptyBalances(IEnumerable<LedgerAccount> accounts) =>
        accounts.ToDictionary(account => account.Id, _ => 0m, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> DefaultAccounts(IEnumerable<LedgerAccount> accounts) =>
        accounts.Where(account => account.IsDefault && !account.IsArchived)
            .GroupBy(account => account.Bucket, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);

    private static void Add(IDictionary<string, decimal> balances, string? accountId, decimal amount)
    {
        if (accountId is not null && balances.ContainsKey(accountId)) balances[accountId] += amount;
    }

    private sealed record LedgerTransactionProjection(
        DateTime Date,
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
