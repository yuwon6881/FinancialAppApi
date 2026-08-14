using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Accounts;

/// <summary>
/// Places an existing bucket leg into one account without changing bucket attribution. The
/// amount supplied to this rule is always CategoryAttributionService.GetCategoryAmount's leg;
/// summing Transaction.Amount here would double-count salary parents and transfers.
/// </summary>
public static class LedgerAccountAttribution
{
    public static decimal GetAccountAmount(
        Transaction transaction,
        LedgerAccount account,
        IReadOnlyDictionary<string, LedgerAccount> accountsById)
    {
        if (string.Equals(transaction.LedgerCategory, "AccountMove", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(account.Id, transaction.AccountId, StringComparison.Ordinal))
                return -Math.Abs(transaction.Amount);
            if (string.Equals(account.Id, transaction.CounterAccountId, StringComparison.Ordinal))
                return Math.Abs(transaction.Amount);
            return 0m;
        }

        var leg = CategoryAttributionService.GetCategoryAmount(transaction, account.Bucket);
        if (leg == 0m) return 0m;

        var placement = GetPlacementAccountId(transaction, account.Bucket, accountsById);
        return string.Equals(placement, account.Id, StringComparison.Ordinal) ? leg : 0m;
    }

    public static string? GetPlacementAccountId(
        Transaction transaction,
        string bucket,
        IReadOnlyDictionary<string, LedgerAccount> accountsById)
    {
        if (IsAccountInBucket(transaction.AccountId, bucket, accountsById))
            return transaction.AccountId;
        if (IsAccountInBucket(transaction.CounterAccountId, bucket, accountsById))
            return transaction.CounterAccountId;
        return null;
    }

    private static bool IsAccountInBucket(
        string? accountId,
        string bucket,
        IReadOnlyDictionary<string, LedgerAccount> accountsById) =>
        accountId is not null
        && accountsById.TryGetValue(accountId, out var account)
        && string.Equals(account.Bucket, bucket, StringComparison.OrdinalIgnoreCase);
}
