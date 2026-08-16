namespace FinancialAppApi.Services;

public sealed record RecurringAccountDebit(
    string Key,
    string AccountId,
    DateOnly OccurrenceDate,
    decimal Amount);

public sealed record RecurringAccountDebitProjection(
    decimal BalanceBefore,
    decimal Shortfall,
    decimal BalanceAfter);

// Projects upcoming auto-deduct debits in date order against the balance available before the
// first debit. The same account may fund several bills, so each debit must consume what the prior
// debit left rather than comparing every bill with the original snapshot.
public static class RecurringAccountBalanceProjection
{
    public static IReadOnlyDictionary<string, RecurringAccountDebitProjection> Project(
        IEnumerable<RecurringAccountDebit> debits,
        IReadOnlyDictionary<string, decimal> openingBalances)
    {
        var remaining = new Dictionary<string, decimal>(openingBalances, StringComparer.Ordinal);
        var projections = new Dictionary<string, RecurringAccountDebitProjection>(StringComparer.Ordinal);

        foreach (var debit in debits
                     .OrderBy(item => item.OccurrenceDate)
                     .ThenBy(item => item.Key, StringComparer.Ordinal))
        {
            if (projections.ContainsKey(debit.Key))
            {
                throw new ArgumentException($"Duplicate recurring debit key '{debit.Key}'.", nameof(debits));
            }

            var balanceBefore = remaining.GetValueOrDefault(debit.AccountId);
            var amount = Math.Abs(debit.Amount);
            var shortfall = Math.Max(0m, amount - balanceBefore);
            var balanceAfter = Math.Max(0m, balanceBefore - amount);
            remaining[debit.AccountId] = balanceAfter;
            projections.Add(debit.Key, new RecurringAccountDebitProjection(
                balanceBefore,
                shortfall,
                balanceAfter));
        }

        return projections;
    }
}
