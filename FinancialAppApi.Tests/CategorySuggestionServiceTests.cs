using FinancialAppApi.Services;
using FinancialAppApi.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class CategorySuggestionServiceTests
{
    [Fact]
    public async Task ReviewCleanupAsync_ReturnsEmptyReviewWhenThereAreNoCategories()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var categoryService = new TransactionCategoryService(context, cache);
        var aiClient = new AiClient(new HttpClient(), TestHelpers.NewConfiguration(), NullLogger<AiClient>.Instance);
        var service = new CategorySuggestionService(aiClient, context, categoryService, cache, new CategoryCleanupApplier(context));

        var result = await service.ReviewCategoryCleanupAsync();

        Assert.Equal(AiOperationStatus.Ok, result.Status);
        Assert.Empty(result.Data!.Suggestions);
    }

    [Fact]
    public async Task SuggestAsync_UsesPriorExactDescriptionWithoutAiConfiguration()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.TransactionCategories.Add(new TransactionCategory { Id = "food", Name = "Food" });
        context.Transactions.Add(new Transaction
        {
            Id = "tx-1",
            Description = "Village Grocer",
            Category = "Food",
            LedgerCategory = "Essentials",
            Amount = 20,
            Date = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var categoryService = new TransactionCategoryService(context, cache);
        var aiClient = new AiClient(new HttpClient(), TestHelpers.NewConfiguration(), NullLogger<AiClient>.Instance);
        var service = new CategorySuggestionService(aiClient, context, categoryService, cache, new CategoryCleanupApplier(context));

        var result = await service.SuggestAsync("village grocer", "outflow", ["Food"]);

        Assert.Equal(AiOperationStatus.Ok, result.Status);
        var suggestion = Assert.Single(result.Data!);
        Assert.Equal("Food", suggestion.Category);
        Assert.True(suggestion.Confidence >= 0.9);
    }

    [Fact]
    public async Task ReviewCleanupAsync_ReturnsDeterministicUnusedCategoryWithoutAi()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.TransactionCategories.Add(new TransactionCategory { Id = "unused", Name = "Unused" });
        await context.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var categoryService = new TransactionCategoryService(context, cache);
        var aiClient = new AiClient(new HttpClient(), TestHelpers.NewConfiguration(), NullLogger<AiClient>.Instance);
        var service = new CategorySuggestionService(aiClient, context, categoryService, cache, new CategoryCleanupApplier(context));

        var result = await service.ReviewCategoryCleanupAsync();

        Assert.Equal(AiOperationStatus.Ok, result.Status);
        var suggestion = Assert.Single(result.Data!.Suggestions);
        Assert.Equal("delete", suggestion.Type);
        Assert.Equal(["Unused"], suggestion.Categories);
    }

    [Fact]
    public void ParseSuggestions_ReturnsTopThreeCanonicalCategories()
    {
        var result = CategorySuggestionService.ParseSuggestions(
            """
            {
              "suggestions": [
                { "category": "food", "confidence": 0.93 },
                { "category": "Transport", "confidence": 0.65 },
                { "category": "Other", "confidence": 0.2 },
                { "category": "Software", "confidence": 0.8 }
              ]
            }
            """,
            ["Food", "Transport", "Software", "Other"]);

        Assert.Equal(["Food", "Software", "Transport"], result.Select(s => s.Category).ToArray());
        Assert.Equal(0.93, result[0].Confidence, 3);
    }

    [Fact]
    public void ParseSuggestions_RejectsUnknownAndDuplicateCategories()
    {
        var result = CategorySuggestionService.ParseSuggestions(
            """
            {
              "suggestions": [
                { "category": "Made Up", "confidence": 0.99 },
                { "category": "Food", "confidence": 99 },
                { "category": "food", "confidence": 0.1 }
              ]
            }
            """,
            ["Food"]);

        var suggestion = Assert.Single(result);
        Assert.Equal("Food", suggestion.Category);
        Assert.Equal(0.99, suggestion.Confidence, 3);
    }
}
