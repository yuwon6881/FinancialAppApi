using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Accounts;

namespace FinancialAppApi.Tests;

public sealed class LedgerAccountBalanceServiceTests
{
    [Fact]
    public async Task SnapshotCalculatesCurrentAndCutoffBalancesFromTheSameHistory()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var essentials = Account("essentials", "Essentials");
        var rewards = Account("rewards", "Rewards");
        context.LedgerAccounts.AddRange(essentials, rewards);
        context.Transactions.AddRange(
            Transaction("before", "Transfer:Essentials->Rewards", 50m, new DateOnly(2026, 7, 1), essentials.Id, rewards.Id),
            Transaction("after", "Rewards", -10m, new DateOnly(2026, 8, 1), rewards.Id));
        await context.SaveChangesAsync();

        var snapshot = await new LedgerAccountBalanceService(context).GetBalanceSnapshotAsync(
            [essentials, rewards],
            TransactionDate.StartOfDate(new DateOnly(2026, 8, 1)));

        Assert.Equal(-50m, snapshot.ThroughExclusive[essentials.Id]);
        Assert.Equal(50m, snapshot.ThroughExclusive[rewards.Id]);
        Assert.Equal(-50m, snapshot.Current[essentials.Id]);
        Assert.Equal(40m, snapshot.Current[rewards.Id]);
    }

    private static LedgerAccount Account(string id, string bucket) => new()
    {
        Id = id,
        Name = id,
        Bucket = bucket,
        Kind = LedgerAccountKind.Bank,
    };

    private static Transaction Transaction(
        string id,
        string ledgerCategory,
        decimal amount,
        DateOnly date,
        string? accountId,
        string? counterAccountId = null) => new()
    {
        Id = id,
        Date = TransactionDate.StartOfDate(date),
        PostedAt = DateTime.UtcNow,
        Description = id,
        Category = "Test",
        LedgerCategory = ledgerCategory,
        Amount = amount,
        AccountId = accountId,
        CounterAccountId = counterAccountId,
    };
}
