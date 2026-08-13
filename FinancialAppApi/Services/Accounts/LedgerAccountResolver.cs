using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Accounts;

/// <summary>
/// Resolves omitted account legs for clients that predate account tracking. Once the account
/// migration has run every user has a live default per bucket, so this is compatibility for old
/// PWAs rather than a second balance source of truth.
/// </summary>
public sealed class LedgerAccountResolver
{
    private readonly AppDbContext _context;
    private IReadOnlyDictionary<string, string>? _defaultAccountIdByBucket;

    public LedgerAccountResolver(AppDbContext context)
    {
        _context = context;
    }

    public async Task ResolveMissingAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        if (!IsBucket(transaction.LedgerCategory)
            && !transaction.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase))
            return;
        if (_defaultAccountIdByBucket is null)
        {
            var rows = await _context.LedgerAccounts
                .AsNoTracking()
                .Where(account => account.IsDefault && !account.IsArchived)
                .Select(account => new { account.Bucket, account.Id })
                .ToListAsync(cancellationToken);
            _defaultAccountIdByBucket = rows
                .GroupBy(account => account.Bucket, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);
        }
        var defaults = _defaultAccountIdByBucket;

        ResolveMissing(transaction, defaults);
    }

    public static void ResolveMissing(
        Transaction transaction,
        IReadOnlyDictionary<string, string> defaultAccountIdByBucket)
    {
        if (string.Equals(transaction.LedgerCategory, "AccountMove", StringComparison.OrdinalIgnoreCase))
            return;

        if (IsBucket(transaction.LedgerCategory))
        {
            transaction.AccountId ??= GetDefault(defaultAccountIdByBucket, transaction.LedgerCategory);
            return;
        }

        if (!transaction.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase))
            return;

        var parts = transaction.LedgerCategory["Transfer:".Length..].Split("->", StringSplitOptions.None);
        if (parts.Length != 2) return;

        var source = parts[0].Trim();
        var target = parts[1].Trim();
        var sourceIsBucket = IsBucket(source);
        var targetIsBucket = IsBucket(target);

        if (sourceIsBucket)
            transaction.AccountId ??= GetDefault(defaultAccountIdByBucket, source);

        if (targetIsBucket)
        {
            // Income splits have only one bucket leg. Keep that leg in AccountId so old
            // attribution and CSV consumers continue to read the same field.
            if (source.Equals("Income", StringComparison.OrdinalIgnoreCase))
                transaction.AccountId ??= GetDefault(defaultAccountIdByBucket, target);
            else
                transaction.CounterAccountId ??= GetDefault(defaultAccountIdByBucket, target);
        }
    }

    public static bool IsBucket(string? value) =>
        value is not null && FinancialConstants.BudgetCategories.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static string? GetDefault(IReadOnlyDictionary<string, string> defaults, string bucket) =>
        defaults.TryGetValue(bucket, out var id) ? id : null;
}
