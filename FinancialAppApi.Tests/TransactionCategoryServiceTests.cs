using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FinancialAppApi.Tests;

public class TransactionCategoryServiceTests
{
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

    private static TransactionCategoryService NewService(Database.AppDbContext context)
    {
        return new TransactionCategoryService(context, new MemoryCache(new MemoryCacheOptions()));
    }
}
