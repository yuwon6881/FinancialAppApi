using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class VaultDocumentTypeServiceTests
{
    [Fact]
    public async Task ListAndReviewAsync_ReturnEmptyCollectionsWhenThereAreNoTypes()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = NewService(context, cache);

        var types = await service.ListAsync();
        var suggestions = await service.ReviewAsync();

        Assert.Empty(types);
        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task ListAsync_ReturnsAlphabeticalTypesWithUsageCounts()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.VaultDocumentTypes.AddRange(
            new VaultDocumentTypeDefinition { Id = "invoice", Name = "Invoice" },
            new VaultDocumentTypeDefinition { Id = "receipt", Name = "Receipt" });
        context.VaultDocuments.AddRange(
            NewDocument("Receipt", "one.pdf"),
            NewDocument("Receipt", "two.pdf"));
        await context.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = NewService(context, cache);

        var result = await service.ListAsync();

        Assert.Collection(
            result,
            item =>
            {
                Assert.Equal("Invoice", item.Name);
                Assert.Equal(0, item.UsageCount);
            },
            item =>
            {
                Assert.Equal("Receipt", item.Name);
                Assert.Equal(2, item.UsageCount);
            });
    }

    [Fact]
    public async Task DeleteAsync_DeletesUnusedType()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.VaultDocumentTypes.Add(new VaultDocumentTypeDefinition { Id = "invoice", Name = "Invoice" });
        await context.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = NewService(context, cache);

        var result = await service.DeleteAsync("invoice", null);

        Assert.True(result.Success);
        Assert.Empty(context.VaultDocumentTypes);
    }

    [Fact]
    public async Task DeleteAsync_ReassignsDocumentsAndDeletesUsedTypeInOneSave()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.VaultDocumentTypes.AddRange(
            new VaultDocumentTypeDefinition { Id = "invoice", Name = "Invoice" },
            new VaultDocumentTypeDefinition { Id = "other", Name = "Other" });
        context.VaultDocuments.Add(NewDocument("Invoice", "one.pdf"));
        await context.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = NewService(context, cache);

        var result = await service.DeleteAsync("invoice", "other");

        Assert.True(result.Success);
        Assert.DoesNotContain(context.VaultDocumentTypes, type => type.Id == "invoice");
        Assert.Equal("Other", (await context.VaultDocuments.SingleAsync()).DocumentType);
    }

    private static VaultDocumentTypeService NewService(
        Database.AppDbContext context,
        IMemoryCache cache) =>
        new(
            context,
            new AiClient(
                new HttpClient(),
                TestHelpers.NewConfiguration(),
                NullLogger<AiClient>.Instance),
            cache);

    private static VaultDocument NewDocument(string type, string fileName) => new()
    {
        StorageObjectPath = $"test/{fileName}",
        OriginalFileName = fileName,
        ContentType = "application/pdf",
        SizeBytes = 10,
        Sha256 = new string('A', 64),
        TaxYear = 2026,
        DocumentType = type,
        UploadedAt = DateTime.UtcNow,
        RetentionUntil = new DateOnly(2033, 12, 31)
    };
}
