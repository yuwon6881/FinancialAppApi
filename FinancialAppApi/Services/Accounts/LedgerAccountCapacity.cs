using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Accounts;

// What an account can still pay out. A bank-like account can pay its balance; a credit card can
// pay up to its limit beyond what it already owes. A card with no recorded limit has no known
// capacity, so it is left out rather than treated as empty -- an unknown limit is not a zero one.
public static class LedgerAccountCapacity
{
    public static bool IsUnknown(LedgerAccount? account) =>
        account is not null && LedgerAccountKind.IsCreditCard(account.Kind) && account.CreditLimit is null;

    public static Dictionary<string, decimal> ForAccounts(
        IEnumerable<LedgerAccount> accounts,
        IReadOnlyDictionary<string, decimal> balances)
    {
        var capacities = new Dictionary<string, decimal>(balances, StringComparer.Ordinal);
        foreach (var account in accounts.Where(account => LedgerAccountKind.IsCreditCard(account.Kind)))
        {
            if (account.CreditLimit is { } limit)
                capacities[account.Id] = limit + balances.GetValueOrDefault(account.Id);
            else
                capacities.Remove(account.Id);
        }
        return capacities;
    }
}
