using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Accounts;
using FinancialAppApi.Services;

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

    // The snapshot-resumed answer and the full-history scan must agree exactly -- the whole point of
    // caching per-cycle account balances is that it changes the cost of the answer, not the answer.
    // Warming the cycle cache first is what puts the two paths on different code, so this fails if
    // the resumed path ever drifts from the scan it replaced.
    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    public async Task ResumingFromTheCycleCacheMatchesTheFullHistoryScan(int cycleDay)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            UserId = TestHelpers.DefaultUserId,
            SelectedMonth = "Aug",
            SelectedYear = 2026,
            CycleDay = cycleDay,
        });
        var essentials = Account("essentials", "Essentials");
        var rewards = Account("rewards", "Rewards");
        context.LedgerAccounts.AddRange(essentials, rewards);
        context.Transactions.AddRange(
            Transaction("jan", "Essentials", -25m, new DateOnly(2026, 1, 20), essentials.Id),
            Transaction("mar", "Transfer:Essentials->Rewards", 40m, new DateOnly(2026, 3, 4), essentials.Id, rewards.Id),
            Transaction("jun", "Rewards", -15m, new DateOnly(2026, 6, 11), rewards.Id),
            Transaction("move", "AccountMove", 12m, new DateOnly(2026, 7, 2), essentials.Id, rewards.Id),
            Transaction("aug", "Essentials", -30m, new DateOnly(2026, 8, 9), essentials.Id),
            Transaction("future", "Rewards", -5m, new DateOnly(2027, 2, 1), rewards.Id));
        await context.SaveChangesAsync();

        var scanned = await new LedgerAccountBalanceService(context).GetBalancesAsync(
            [essentials, rewards]);

        // Populate the per-cycle cache, then ask again so the resumed path is the one under test.
        var cycleBalanceService = new CycleBalanceService(context);
        await cycleBalanceService.EnsureComputedThroughAsync(2026, 12, cycleDay);
        var resumed = await new LedgerAccountBalanceService(context, cycleBalanceService)
            .GetBalancesAsync([essentials, rewards]);

        Assert.Equal(scanned[essentials.Id], resumed[essentials.Id]);
        Assert.Equal(scanned[rewards.Id], resumed[rewards.Id]);

        // Stated outright so the comparison above cannot pass by both paths returning nothing, and
        // so the totals are pinned independently of the cycle day -- an all-time balance counts the
        // same rows however the cycle boundaries fall.
        Assert.Equal(-107m, resumed[essentials.Id]);
        Assert.Equal(32m, resumed[rewards.Id]);
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
