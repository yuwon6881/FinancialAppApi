using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Accounts;

/// <summary>
/// The single definition of how one transaction moves account balances. Shared by
/// <see cref="LedgerAccountBalanceService"/>, which answers reads, and
/// <see cref="CycleBalanceService"/>, which caches the running totals at cycle boundaries -- the
/// two must not drift, or a cached balance would stop matching a rescanned one.
/// </summary>
public static class LedgerAccountBalanceMath
{
    public static readonly string[] LedgerBuckets = ["Essentials", "Growth", "Stability", "Rewards"];

    /// <summary>
    /// Applies one transaction to <paramref name="balances"/>, creating an entry for any account
    /// known to <paramref name="accountsById"/>. Callers that want a fixed key set project the
    /// result afterwards rather than pre-seeding, so that placement is always decided against the
    /// complete account set.
    /// </summary>
    public static void Accumulate(
        IDictionary<string, decimal> balances,
        Transaction transaction,
        IReadOnlyDictionary<string, LedgerAccount> accountsById)
    {
        if (string.Equals(transaction.LedgerCategory, "AccountMove", StringComparison.OrdinalIgnoreCase))
        {
            Add(balances, transaction.AccountId, -Math.Abs(transaction.Amount), accountsById);
            Add(balances, transaction.CounterAccountId, Math.Abs(transaction.Amount), accountsById);
            return;
        }

        foreach (var bucket in LedgerBuckets)
        {
            var leg = CategoryAttributionService.GetCategoryAmount(transaction, bucket);
            if (leg == 0m) continue;
            Add(
                balances,
                LedgerAccountAttribution.GetPlacementAccountId(transaction, bucket, accountsById),
                leg,
                accountsById);
        }
    }

    private static void Add(
        IDictionary<string, decimal> balances,
        string? accountId,
        decimal amount,
        IReadOnlyDictionary<string, LedgerAccount> accountsById)
    {
        if (accountId is null || !accountsById.ContainsKey(accountId)) return;
        balances[accountId] = (balances.TryGetValue(accountId, out var existing) ? existing : 0m) + amount;
    }
}
