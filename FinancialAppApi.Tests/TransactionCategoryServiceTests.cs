using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FinancialAppApi.Tests;

public class TransactionCategoryServiceTests
{
    [Fact]
    public async Task GetCategoriesAsync_ReturnsEmptyCollectionWhenThereAreNoCategories()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context);

        var categories = await service.GetCategoriesAsync();

        Assert.Empty(categories);
    }

    [Fact]
    public async Task GetCategoriesAsync_ReturnsCategoriesOrderedByName()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.TransactionCategories.AddRange(
            new TransactionCategory { Id = "cat-z", Name = "Zed" },
            new TransactionCategory { Id = "cat-a", Name = "Alpha" });
        await context.SaveChangesAsync();

        var service = NewService(context);

        var categories = await service.GetCategoriesAsync();

        Assert.Equal(new[] { "Alpha", "Zed" }, categories.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task CreateCategoryAsync_CreatesCategoryAndAssignsId()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context);
        var category = new TransactionCategory { Name = "Groceries" };

        var result = await service.CreateCategoryAsync(category);

        Assert.Equal(CreateTransactionCategoryStatus.Created, result.Status);
        Assert.StartsWith("cat-", result.Category!.Id);
        Assert.True(await context.TransactionCategories.AnyAsync(c => c.Name == "Groceries"));
    }

    [Fact]
    public async Task CreateCategoryAsync_DedupesReplayOnClientId()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.TransactionCategories.Add(new TransactionCategory { Id = "cat-fixed", Name = "Groceries" });
        await context.SaveChangesAsync();
        var service = NewService(context);

        // Replay of the same offline create (same client id) resolves to the existing row as
        // success rather than a duplicate-name failure for the client's own pending write.
        var result = await service.CreateCategoryAsync(new TransactionCategory { Id = "cat-fixed", Name = "Groceries" });

        Assert.Equal(CreateTransactionCategoryStatus.Existing, result.Status);
        Assert.Equal("cat-fixed", result.Category!.Id);
        Assert.Equal(1, await context.TransactionCategories.CountAsync());
    }

    [Fact]
    public async Task CreateCategoryAsync_RejectsDuplicateName()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.TransactionCategories.Add(new TransactionCategory { Id = "cat-food", Name = "Food" });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.CreateCategoryAsync(new TransactionCategory { Name = "food" });

        Assert.Equal(CreateTransactionCategoryStatus.DuplicateName, result.Status);
        Assert.Equal("Category 'food' already exists.", result.Message);
    }

    [Fact]
    public async Task DeleteCategoryAsync_DeletesCategory()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.TransactionCategories.Add(new TransactionCategory { Id = "cat-food", Name = "Food" });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.DeleteCategoryAsync("cat-food");

        Assert.Equal(DeleteTransactionCategoryStatus.Deleted, result.Status);
        Assert.False(await context.TransactionCategories.AnyAsync(c => c.Id == "cat-food"));
    }

    [Fact]
    public async Task DeleteCategoryAsync_RequiresReplacementWhenCategoryIsInUse()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.TransactionCategories.AddRange(
            new TransactionCategory { Id = "cat-food", Name = "Food" },
            new TransactionCategory { Id = "cat-other", Name = "Other" });
        context.Transactions.Add(NewTransaction("tx-1", "Food"));
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.DeleteCategoryAsync("cat-food");

        Assert.Equal(DeleteTransactionCategoryStatus.InUse, result.Status);
        Assert.Equal(1, result.TransactionCount);
        Assert.True(await context.TransactionCategories.AnyAsync(c => c.Id == "cat-food"));
    }

    [Fact]
    public async Task DeleteCategoryAsync_TransfersTransactionsAndRecurringPaymentsBeforeDelete()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.TransactionCategories.AddRange(
            new TransactionCategory { Id = "cat-food", Name = "Food" },
            new TransactionCategory { Id = "cat-other", Name = "Other" });
        context.Transactions.Add(NewTransaction("tx-1", "Food"));
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "rec-1",
            Name = "Lunch plan",
            Amount = 50m,
            Frequency = "Monthly",
            Category = "Food",
            LedgerCategory = "Essentials",
            NextDueDate = "2026-07-15",
            DueDate = 15,
            StartDate = "2026-01-01",
            Active = true
        });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var result = await service.DeleteCategoryAsync("cat-food", "cat-other");

        Assert.Equal(DeleteTransactionCategoryStatus.Deleted, result.Status);
        Assert.False(await context.TransactionCategories.AnyAsync(c => c.Id == "cat-food"));
        Assert.Equal("Other", (await context.Transactions.SingleAsync()).Category);
        Assert.Equal("Other", (await context.RecurringPayments.SingleAsync()).Category);
    }

    [Fact]
    public async Task UpdateCycleLimitAsync_UpsertsAnEffectiveDatedGuideAndCanDisableIt()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.TransactionCategories.Add(new TransactionCategory { Id = "cat-transport", Name = "Transport" });
        await context.SaveChangesAsync();
        var service = NewService(context, new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));

        var enabled = await service.UpdateCycleLimitAsync("cat-transport", 400m);
        var revised = await service.UpdateCycleLimitAsync("cat-transport", 450m);

        Assert.Equal(UpdateCategoryCycleLimitStatus.Updated, enabled.Status);
        Assert.Equal(UpdateCategoryCycleLimitStatus.Updated, revised.Status);
        Assert.Equal(450m, (await context.TransactionCategories.SingleAsync()).CycleLimit);
        var guide = await context.CategorySpendingGuides.SingleAsync();
        Assert.Equal("2026-07", guide.EffectiveFromCycleKey);
        Assert.Equal(450m, guide.LimitAmount);

        var disabled = await service.UpdateCycleLimitAsync("cat-transport", null);

        Assert.Equal(UpdateCategoryCycleLimitStatus.Updated, disabled.Status);
        Assert.Null((await context.TransactionCategories.SingleAsync()).CycleLimit);
        Assert.Null((await context.CategorySpendingGuides.SingleAsync()).LimitAmount);
    }

    [Fact]
    public async Task DeleteCategoryAsync_ClosesItsGuideWithoutRewritingEarlierCycles()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.TransactionCategories.Add(new TransactionCategory
        {
            Id = "cat-transport",
            Name = "Transport",
            CycleLimit = 400m
        });
        context.CategorySpendingGuides.Add(new CategorySpendingGuide
        {
            Id = "guide-old",
            CategoryName = "Transport",
            EffectiveFromCycleKey = "2026-06",
            LimitAmount = 400m
        });
        await context.SaveChangesAsync();
        var service = NewService(context, new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));

        var result = await service.DeleteCategoryAsync("cat-transport");

        Assert.Equal(DeleteTransactionCategoryStatus.Deleted, result.Status);
        var guides = await context.CategorySpendingGuides
            .OrderBy(guide => guide.EffectiveFromCycleKey)
            .ToListAsync();
        Assert.Equal(2, guides.Count);
        Assert.Equal(400m, guides[0].LimitAmount);
        Assert.Equal("2026-07", guides[1].EffectiveFromCycleKey);
        Assert.Null(guides[1].LimitAmount);
    }

    private static TransactionCategoryService NewService(
        Database.AppDbContext context,
        DateTimeOffset? utcNow = null)
    {
        var clock = utcNow.HasValue
            ? new FinancialClock(
                TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")),
                new FixedTimeProvider(utcNow.Value))
            : null;
        return new TransactionCategoryService(context, new MemoryCache(new MemoryCacheOptions()), clock);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static Transaction NewTransaction(string id, string category)
    {
        return new Transaction
        {
            Id = id,
            Date = DateTime.UtcNow,
            Description = "Test transaction",
            Category = category,
            LedgerCategory = "Essentials",
            Amount = -10m
        };
    }
}
