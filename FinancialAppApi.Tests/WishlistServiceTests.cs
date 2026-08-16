using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests;

public class WishlistServiceTests
{
    [Fact]
    public async Task GetWishlistAsync_ReturnsActiveItemsFirst()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context);
        context.WishlistItems.AddRange(
            NewItem(1, "Later", active: false, createdAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            NewItem(2, "Active", active: true, createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var items = await service.GetWishlistAsync();

        Assert.Equal("Active", items[0].Name);
    }

    [Fact]
    public async Task CreateWishlistItemAsync_MakesFirstItemActiveByDefault()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context);

        var result = await service.CreateWishlistItemAsync(NewItem(0, "Camera", active: false));

        Assert.Equal(WishlistMutationStatus.Success, result.Status);
        Assert.True(result.Item!.IsActive);
        Assert.True(await context.WishlistItems.AnyAsync(w => w.Name == "Camera"));
    }

    [Fact]
    public async Task CreateWishlistItemAsync_DedupesReplayOnClientKey()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context);

        var first = NewItem(0, "Camera", active: false);
        first.ClientKey = "op-abc";
        var firstResult = await service.CreateWishlistItemAsync(first);

        // Replay of the same offline create (e.g. lost-response retry): same client key must
        // resolve to the already-created row, not insert a duplicate.
        var second = NewItem(0, "Camera", active: false);
        second.ClientKey = "op-abc";
        var secondResult = await service.CreateWishlistItemAsync(second);

        Assert.Equal(WishlistMutationStatus.Success, firstResult.Status);
        Assert.Equal(WishlistMutationStatus.Success, secondResult.Status);
        Assert.Equal(firstResult.Item!.Id, secondResult.Item!.Id);
        Assert.Equal(1, await context.WishlistItems.CountAsync());
    }

    [Fact]
    public async Task UpdateWishlistItemAsync_ActivatesItemAndDeactivatesOtherItems()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.WishlistItems.AddRange(
            NewItem(1, "Current", active: true),
            NewItem(2, "Next", active: false));
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.UpdateWishlistItemAsync(2, NewItem(2, "Next", active: true));

        Assert.Equal(WishlistMutationStatus.Success, result.Status);
        Assert.False((await context.WishlistItems.FindAsync(1))!.IsActive);
        Assert.True((await context.WishlistItems.FindAsync(2))!.IsActive);
    }

    [Fact]
    public async Task DeleteWishlistItemAsync_DeletesItemAndPromotesNextWhenActive()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.WishlistItems.AddRange(
            NewItem(1, "Old", active: true, createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            NewItem(2, "New", active: false, createdAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.DeleteWishlistItemAsync(1);

        Assert.Equal(WishlistMutationStatus.Success, result);
        Assert.Null(await context.WishlistItems.FindAsync(1));
        Assert.True((await context.WishlistItems.FindAsync(2))!.IsActive);
    }

    [Fact]
    public async Task DeleteWishlistItemAsync_DoesNotPromotePurchasedItem()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var purchased = NewItem(2, "Already bought", active: false);
        purchased.IsPurchased = true;
        context.WishlistItems.AddRange(NewItem(1, "Current", active: true), purchased);
        await context.SaveChangesAsync();

        await NewService(context).DeleteWishlistItemAsync(1);

        Assert.False((await context.WishlistItems.FindAsync(2))!.IsActive);
    }

    [Fact]
    public async Task DeleteWishlistItemAsync_RemovesOrphanPurchaseTransaction()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var purchased = NewItem(1, "Headphones", active: false, price: 80m);
        purchased.IsPurchased = true;
        purchased.PurchaseTransactionId = "tx-1";
        context.WishlistItems.Add(purchased);
        context.Transactions.Add(new Transaction
        {
            Id = "tx-1",
            Date = DateTime.UtcNow,
            Description = "Purchased: Headphones (Wish List)",
            Category = "Other",
            LedgerCategory = "Rewards",
            Amount = -80m,
            WishlistItemId = 1
        });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.DeleteWishlistItemAsync(1);

        Assert.Equal(WishlistMutationStatus.Success, result);
        Assert.Null(await context.WishlistItems.FindAsync(1));
        // The ledger transaction must be gone too, not orphaned.
        Assert.Null(await context.Transactions.FindAsync("tx-1"));
    }

    [Fact]
    public async Task PurchaseWishlistItemAsync_MarksPurchasedAndCreatesTransaction()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.WishlistItems.Add(NewItem(1, "Headphones", active: true, price: 80m));
        context.Transactions.Add(new Transaction { Id = "reward", Date = DateTime.UtcNow, Amount = 1000m, LedgerCategory = "Rewards" });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.PurchaseWishlistItemAsync(1, accountId: "acct-rewards");

        Assert.Equal(WishlistMutationStatus.Success, result.Status);
        Assert.True(result.Item!.IsPurchased);
        Assert.False(result.Item.IsActive);
        Assert.Equal(-80m, result.Transaction!.Amount);
        Assert.True(await context.Transactions.AnyAsync(t => t.WishlistItemId == 1));
    }

    [Fact]
    public async Task PurchaseWishlistItemAsync_RejectsFutureClaimDate()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.WishlistItems.Add(NewItem(1, "Headphones", active: true, price: 80m));
        context.Transactions.Add(new Transaction { Id = "reward", Date = DateTime.UtcNow, Amount = 1000m, LedgerCategory = "Rewards" });
        await context.SaveChangesAsync();

        var result = await NewService(context).PurchaseWishlistItemAsync(
            1,
            DateTime.UtcNow.Date.AddDays(1),
            accountId: "acct-rewards");

        Assert.Equal(WishlistMutationStatus.DateInvalid, result.Status);
        Assert.False((await context.WishlistItems.FindAsync(1))!.IsPurchased);
        Assert.False(await context.Transactions.AnyAsync(t => t.WishlistItemId == 1));
    }

    [Fact]
    public async Task PurchaseWishlistItemAsync_ReplayReturnsTheOriginalTransaction()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.WishlistItems.Add(NewItem(1, "Headphones", active: true, price: 80m));
        context.Transactions.Add(new Transaction { Id = "reward", Date = DateTime.UtcNow, Amount = 1000m, LedgerCategory = "Rewards" });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var first = await service.PurchaseWishlistItemAsync(1, accountId: "acct-rewards");
        var replay = await service.PurchaseWishlistItemAsync(1, accountId: "acct-rewards");

        Assert.Equal(WishlistMutationStatus.Success, replay.Status);
        Assert.Equal(first.Transaction!.Id, replay.Transaction!.Id);
        Assert.Equal(1, await context.Transactions.CountAsync(t => t.WishlistItemId == 1));
    }

    [Fact]
    public async Task PurchaseWishlistItemAsync_PromotesExactlyOneUnpurchasedItem()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var purchased = NewItem(3, "Purchased", active: true);
        purchased.IsPurchased = true;
        context.WishlistItems.AddRange(
            NewItem(1, "Current", active: true),
            NewItem(2, "Next", active: false, createdAt: DateTime.UtcNow.AddMinutes(1)),
            purchased);
        context.Transactions.Add(new Transaction { Id = "reward", Date = DateTime.UtcNow, Amount = 1000m, LedgerCategory = "Rewards" });
        await context.SaveChangesAsync();

        await NewService(context).PurchaseWishlistItemAsync(1, accountId: "acct-rewards");

        var active = await context.WishlistItems.Where(w => w.IsActive).ToListAsync();
        Assert.Single(active);
        Assert.Equal(2, active[0].Id);
        Assert.False(active[0].IsPurchased);
    }

    [Fact]
    public async Task UnpurchaseWishlistItemAsync_RemovesPurchaseTransactionAndReactivatesItem()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var item = NewItem(1, "Headphones", active: false, price: 80m);
        item.IsPurchased = true;
        item.PurchaseTransactionId = "tx-1";
        context.WishlistItems.Add(item);
        context.Transactions.Add(new Transaction
        {
            Id = "tx-1",
            Date = DateTime.UtcNow,
            Description = "Purchased: Headphones (Wish List)",
            Category = "Other",
            LedgerCategory = "Rewards",
            Amount = -80m,
            WishlistItemId = 1
        });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.UnpurchaseWishlistItemAsync(1);

        Assert.Equal(WishlistMutationStatus.Success, result.Status);
        Assert.False(result.Item!.IsPurchased);
        Assert.True(result.Item.IsActive);
        Assert.Null(await context.Transactions.FindAsync("tx-1"));
    }

    [Fact]
    public async Task UnpurchaseWishlistItemAsync_FindsLegacyTransactionByWishlistItemId()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var item = NewItem(1, "Headphones", active: false, price: 80m);
        item.IsPurchased = true;
        item.PurchaseTransactionId = null;
        context.WishlistItems.Add(item);
        context.Transactions.Add(new Transaction
        {
            Id = "legacy-purchase",
            Date = DateTime.UtcNow,
            Description = "Purchased: Headphones (Wish List)",
            Category = "Other",
            LedgerCategory = "Rewards",
            Amount = -80m,
            WishlistItemId = 1
        });
        await context.SaveChangesAsync();

        var result = await NewService(context).UnpurchaseWishlistItemAsync(1);

        Assert.Equal(WishlistMutationStatus.Success, result.Status);
        Assert.Null(await context.Transactions.FindAsync("legacy-purchase"));
    }

    private static WishlistService NewService(Database.AppDbContext context)
    {
        if (context.LedgerAccounts.All(account => account.Id != "acct-rewards"))
        {
            context.LedgerAccounts.Add(new LedgerAccount
            {
                Id = "acct-rewards",
                Name = "Rewards account",
                Bucket = "Rewards",
                Kind = LedgerAccountKind.EWallet,
            });
            context.SaveChanges();
        }
        var cycleBalanceService = new CycleBalanceService(context);
        var savingsGoalService = new FinancialAppApi.Services.SavingsGoals.SavingsGoalService(context, cycleBalanceService);
        return new WishlistService(context, cycleBalanceService, savingsGoalService);
    }

    private static WishlistItem NewItem(
        int id,
        string name,
        bool active,
        decimal price = 100m,
        DateTime? createdAt = null)
    {
        return new WishlistItem
        {
            Id = id,
            Name = name,
            Price = price,
            Priority = "Medium",
            IsActive = active,
            CreatedAt = createdAt ?? DateTime.UtcNow
        };
    }
}
