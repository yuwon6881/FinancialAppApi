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
        context.Transactions.Add(Tx("jan", 2026, 1, 15, 500m, "Essentials"));
        await context.SaveChangesAsync();
        var service = new CycleBalanceService(context);

        var before = await service.GetOpeningBalanceAsync(2026, 2, CycleDay);
        Assert.Equal(500m, before.essentials);

        // Shifting CycleDay moves every cycle boundary retroactively; the Jan 15 row now falls in
        // the cycle that opens Feb, so it is no longer carried into February's opening balance.
        await service.InvalidateAllAsync();

        var after = await service.GetOpeningBalanceAsync(2026, 2, cycleDay: 20);

        Assert.Equal(0m, after.essentials);
    }
}
