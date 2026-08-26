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
    public async Task ReviewCleanupAsync_KeepsDistinctAddSuggestionsAndDropsReservedNames()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.TransactionCategories.Add(new TransactionCategory { Id = "food", Name = "Food" });
        context.Transactions.AddRange(Enumerable.Range(0, 5).Select(index => new Transaction
        {
            Id = $"tx-{index}",
            Description = "Village Grocer",
            Category = "Food",
            LedgerCategory = "Essentials",
            Amount = 20,
            Date = DateTime.UtcNow
        }));
        await context.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new StubAiHandler(
            """
            {
              "suggestions": [
                { "type": "add", "title": "Add Transport", "summary": "Trips are uncategorised.", "categories": [], "targetCategory": null, "newCategoryName": "Transport", "confidence": 0.8 },
                { "type": "add", "title": "Add Bills", "summary": "Utilities are uncategorised.", "categories": [], "targetCategory": null, "newCategoryName": "Bills", "confidence": 0.7 },
                { "type": "add", "title": "Add Transfer", "summary": "Moves between accounts.", "categories": [], "targetCategory": null, "newCategoryName": "Transfer", "confidence": 0.9 }
              ]
            }
            """);
        var service = new CategorySuggestionService(
            new AiClient(
                new HttpClient(handler),
                TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")),
                NullLogger<AiClient>.Instance),
            context,
            new TransactionCategoryService(context, cache),
            cache,
            new CategoryCleanupApplier(context));

        var result = await service.ReviewCategoryCleanupAsync();

        Assert.Equal(AiOperationStatus.Ok, result.Status);
        var added = result.Data!.Suggestions.Where(suggestion => suggestion.Type == "add").ToList();
        Assert.Equal(
            ["Transport", "Bills"],
            added.Select(suggestion => suggestion.NewCategoryName ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task ReviewCleanupAsync_ReturnsValidatedFlowCorrectionAndCountsIncompatibleEntries()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.TransactionCategories.Add(new TransactionCategory
        {
            Id = "salary",
            Name = "Salary",
            Type = CategoryFlowType.Outflow
        });
        context.Transactions.AddRange(
            new Transaction { Id = "income", Description = "Payroll", Category = "Salary", LedgerCategory = "Income", Amount = 5000, Date = DateTime.UtcNow },
            new Transaction { Id = "mistake", Description = "Correction", Category = "Salary", LedgerCategory = "Essentials", Amount = -10, Date = DateTime.UtcNow });
        await context.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new StubAiHandler(
            """
            {
              "suggestions": [
                { "type": "changeFlow", "title": "Salary should be money in", "summary": "Salary is normally income.", "categories": ["Salary"], "targetCategory": null, "newCategoryName": null, "sourceFlow": "outflow", "targetFlow": "inflow", "confidence": 0.96 }
              ]
            }
            """);
        var service = new CategorySuggestionService(
            new AiClient(new HttpClient(handler), TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "test-model")), NullLogger<AiClient>.Instance),
            context,
            new TransactionCategoryService(context, cache),
            cache,
            new CategoryCleanupApplier(context));

        var result = await service.ReviewCategoryCleanupAsync();

        var suggestion = Assert.Single(result.Data!.Suggestions, item => item.Type == "changeFlow");
        Assert.Equal("outflow", suggestion.SourceFlow);
        Assert.Equal("inflow", suggestion.TargetFlow);
        Assert.Equal(1, suggestion.AffectedTransactionCount);
    }

    private sealed class StubAiHandler(string text) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "status": "completed",
                      "output": [{ "type": "message", "content": [{ "type": "output_text", "text": {{System.Text.Json.JsonSerializer.Serialize(text)}} }] }]
                    }
                    """,
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
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
