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
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.PurchaseWishlistItemAsync(1);

        Assert.Equal(WishlistMutationStatus.Success, result.Status);
        Assert.True(result.Item!.IsPurchased);
        Assert.False(result.Item.IsActive);
        Assert.Equal(-80m, result.Transaction!.Amount);
        Assert.True(await context.Transactions.AnyAsync(t => t.WishlistItemId == 1));
    }

    [Fact]
    public async Task PurchaseWishlistItemAsync_ReplayReturnsTheOriginalTransaction()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.WishlistItems.Add(NewItem(1, "Headphones", active: true, price: 80m));
        await context.SaveChangesAsync();
        var service = NewService(context);

        var first = await service.PurchaseWishlistItemAsync(1);
        var replay = await service.PurchaseWishlistItemAsync(1);

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
        await context.SaveChangesAsync();

        await NewService(context).PurchaseWishlistItemAsync(1);

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
    public async Task GetClaimedWishlistPagedAsync_ReturnsOnlyPurchasedNewestFirstAndPaged()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        // 6 purchased (claimed on distinct days) + 1 unpurchased that must be excluded.
        for (var i = 1; i <= 6; i++)
        {
            var purchased = NewItem(i, $"Claim {i}", active: false, price: 10m * i);
            purchased.IsPurchased = true;
            purchased.PurchasedAt = new DateTime(2026, 1, i, 0, 0, 0, DateTimeKind.Utc);
            context.WishlistItems.Add(purchased);
        }
        context.WishlistItems.Add(NewItem(7, "Still saving", active: true));
        await context.SaveChangesAsync();
        var service = NewService(context);

        var page1 = await service.GetClaimedWishlistPagedAsync(1, 5);
        var page2 = await service.GetClaimedWishlistPagedAsync(2, 5);

        Assert.Equal(6, page1.Total);
        Assert.Equal(5, page1.Items.Count);
        Assert.All(page1.Items, item => Assert.True(item.IsPurchased));
        // Newest claim (Jan 6) first.
        Assert.Equal("Claim 6", page1.Items[0].Name);
        Assert.Equal("Claim 2", page1.Items[4].Name);
        // Second page holds the remaining, oldest claim.
        Assert.Single(page2.Items);
        Assert.Equal("Claim 1", page2.Items[0].Name);
    }

    [Fact]
    public async Task GetClaimedWishlistPagedAsync_ClampsInvalidPagingArguments()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var purchased = NewItem(1, "Only claim", active: false);
        purchased.IsPurchased = true;
        purchased.PurchasedAt = DateTime.UtcNow;
        context.WishlistItems.Add(purchased);
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.GetClaimedWishlistPagedAsync(0, 0);

        Assert.Equal(1, result.Page);
        Assert.Equal(5, result.PageSize);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task GetClaimedWishlistPagedAsync_HandlesHugePageAndClampsPageSize()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var purchased = NewItem(1, "Only claim", active: false);
        purchased.IsPurchased = true;
        purchased.PurchasedAt = DateTime.UtcNow;
        context.WishlistItems.Add(purchased);
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.GetClaimedWishlistPagedAsync(int.MaxValue, int.MaxValue);

        Assert.Equal(int.MaxValue, result.Page);
        Assert.Equal(100, result.PageSize);
        Assert.Empty(result.Items);
        Assert.Equal(1, result.Total);
    }

    [Fact]
    public async Task GetClaimedWishlistPagedAsync_UsesIdAsStableTimestampTieBreaker()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var id = 1; id <= 3; id++)
        {
            var purchased = NewItem(id, $"Claim {id}", active: false, createdAt: timestamp);
            purchased.IsPurchased = true;
            purchased.PurchasedAt = timestamp;
            context.WishlistItems.Add(purchased);
        }
        await context.SaveChangesAsync();
        var service = NewService(context);

        var firstPage = await service.GetClaimedWishlistPagedAsync(1, 2);
        var secondPage = await service.GetClaimedWishlistPagedAsync(2, 2);

        Assert.Equal([3, 2], firstPage.Items.Select(item => item.Id));
        Assert.Equal([1], secondPage.Items.Select(item => item.Id));
    }

    private static WishlistService NewService(Database.AppDbContext context)
    {
        return new WishlistService(context, new CycleBalanceService(context));
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
