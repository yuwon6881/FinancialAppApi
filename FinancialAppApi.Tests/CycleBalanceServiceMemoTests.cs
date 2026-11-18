using FinancialAppApi.Models;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

// GetOpeningBalanceAsync memoizes per (year, monthIndex, cycleDay) for the life of the request, so
// a single /api/bootstrap resolves the active cycle's opening balance once instead of once per
// caller. These tests pin the part that can silently go wrong: the memo must not outlive an
// invalidation happening in the same request.
public class CycleBalanceServiceMemoTests
{
    private const int CycleDay = 1;

    [Fact]
    public async Task OpeningBalanceIncludesDepositsInCyclesBefore2026()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        const string accountId = "acct-essentials";
        context.LedgerAccounts.Add(new LedgerAccount { Id = accountId, Name = "Cash", Bucket = "Essentials" });
        var deposit = Tx("jan-before-cycle", 2026, 1, 15, 1000m, "Essentials");
        deposit.AccountId = accountId;
        context.Transactions.AddRange(deposit, Tx("older-growth", 2025, 11, 10, 100m, "Growth"));
        await context.SaveChangesAsync();
        var service = new CycleBalanceService(context);

        var opening = await service.GetOpeningBalanceAsync(2026, 1, cycleDay: 28);
        var accounts = await service.GetOpeningAccountBalancesAsync(2026, 1, cycleDay: 28);

        Assert.Equal(1000m, opening.essentials);
        Assert.Equal(100m, opening.growth);
        Assert.NotNull(accounts);
        Assert.Equal(1000m, accounts[accountId]);
    }

    private static Transaction Tx(string id, int year, int month, int day, decimal amount, string ledgerCategory) =>
        new()
        {
            Id = id,
            Date = new DateTime(year, month, day),
            Description = id,
            Category = "Other",
            LedgerCategory = ledgerCategory,
            Amount = amount
        };

    [Fact]
    public async Task GetOpeningBalanceAsync_RepeatedCallsForSameCycleAgree()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.Add(Tx("jan", 2026, 1, 15, 500m, "Essentials"));
        await context.SaveChangesAsync();
        var service = new CycleBalanceService(context);

        var first = await service.GetOpeningBalanceAsync(2026, 2, CycleDay);
        var second = await service.GetOpeningBalanceAsync(2026, 2, CycleDay);

        Assert.Equal(500m, first.essentials);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task GetOpeningBalanceAsync_ReflectsATransactionAddedAfterAnEarlierReadInTheSameRequest()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.Add(Tx("jan", 2026, 1, 15, 500m, "Essentials"));
        await context.SaveChangesAsync();
        var service = new CycleBalanceService(context);

        // The read that populates the memo, standing in for the dashboard half of a request.
        var before = await service.GetOpeningBalanceAsync(2026, 2, CycleDay);
        Assert.Equal(500m, before.essentials);

        // A mutation dated in a cycle the memo covers, invalidated the way every write path does.
        context.Transactions.Add(Tx("jan-2", 2026, 1, 20, 250m, "Essentials"));
        await context.SaveChangesAsync();
        await service.InvalidateFromAsync(2026, 1);

        var after = await service.GetOpeningBalanceAsync(2026, 2, CycleDay);

        Assert.Equal(750m, after.essentials);
    }

    [Fact]
    // cycleDay is part of the memo key, so this passes on the key alone rather than on
    // InvalidateAllAsync clearing the memo -- the clear there is belt-and-braces, since the only
    // caller is the CycleDay setting change, which already lands on a different key.
    public async Task GetOpeningBalanceAsync_IsKeyedOnCycleDayNotJustTheCycle()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.Add(Tx("feb", 2026, 2, 15, 500m, "Essentials"));
        await context.SaveChangesAsync();
        var service = new CycleBalanceService(context);

        var before = await service.GetOpeningBalanceAsync(2026, 2, CycleDay);
        Assert.Equal(0m, before.essentials);

        // With the cycle starting on the 20th, February 15 belongs to the previous cycle.
        await service.InvalidateAllAsync();

        var after = await service.GetOpeningBalanceAsync(2026, 2, cycleDay: 20);

        Assert.Equal(500m, after.essentials);
    }
}
