using FinancialAppApi.Controllers;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Documents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public sealed class DocumentContentDispositionTests
{
    [Theory]
    [InlineData("image/jpeg", "inline")]
    [InlineData("image/png", "inline")]
    [InlineData("image/webp", "inline")]
    [InlineData("image/heic", "inline")]
    [InlineData("image/heif", "inline")]
    [InlineData("application/pdf", "inline")]
    [InlineData("image/svg+xml", "attachment")]
    [InlineData("application/xml", "attachment")]
    [InlineData("application/json", "attachment")]
    public async Task GetContent_UsesNosniffAndSafeDispositionForEveryStoredType(
        string contentType,
        string expectedDisposition)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.VaultDocuments.Add(new VaultDocument
        {
            Id = 1,
            UserId = "test-user",
            OriginalFileName = "receipt.pdf",
            StorageObjectPath = "test-user/2026/receipt.bin",
            ContentType = contentType,
            Sha256 = "hash",
            SizeBytes = 3,
            TaxYear = 2026,
            UploadedAt = DateTime.UtcNow,
            RetentionUntil = new DateOnly(2033, 12, 31),
        });
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        store.Objects["test-user/2026/receipt.bin"] = [1, 2, 3];
        var controller = NewController(context, store);

        var result = await controller.GetContent(1, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(contentType, file.ContentType);
        Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions.ToString());
        Assert.StartsWith(expectedDisposition, controller.Response.Headers.ContentDisposition.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetContent_SanitizesLegacyFilenameControlsInFallbackDisposition()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.VaultDocuments.Add(new VaultDocument
        {
            Id = 1,
            UserId = "test-user",
            OriginalFileName = "old\"name\r\nX-Evil: yes.xml",
            StorageObjectPath = "test-user/2026/legacy.xml",
            ContentType = "application/xml",
            Sha256 = "hash",
            SizeBytes = 1,
            TaxYear = 2026,
            UploadedAt = DateTime.UtcNow,
            RetentionUntil = new DateOnly(2033, 12, 31),
        });
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        store.Objects["test-user/2026/legacy.xml"] = [1];
        var controller = NewController(context, store);

        await controller.GetContent(1, CancellationToken.None);

        var header = controller.Response.Headers.ContentDisposition.ToString();
        Assert.DoesNotContain('\r', header);
        Assert.DoesNotContain('\n', header);
        Assert.Contains("filename=\"old_name__X-Evil: yes.xml\"", header);
        Assert.Contains("filename*=UTF-8''old%22name%0D%0AX-Evil%3A%20yes.xml", header);
    }

    [Fact]
    public async Task Upload_UsesDetectedBytesWhenDeclaredContentTypeAndExtensionDisagree()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.TaxReliefCategoryLimits.Add(new TaxReliefCategoryLimit
        {
            UserId = "test-user",
            CategoryId = "test-category",
            Name = "Test category",
            Limit = 1000,
            TaxYear = 2026,
        });
        await context.SaveChangesAsync();

        var store = new FakeDocumentVaultStore();
        var controller = NewController(context, store);
        var file = new FormFile(new MemoryStream("%PDF-1.7"u8.ToArray()), 0, 7, "file", "spoof.png")
        {
            Headers = new HeaderDictionary { ["Content-Type"] = "image/png" },
        };

        var result = await controller.Upload(
            file,
            2026,
            null,
            null,
            "test-category",
            null,
            null,
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("application/pdf", Assert.Single(context.VaultDocuments).ContentType);
    }

    private static DocumentsController NewController(AppDbContext context, FakeDocumentVaultStore store)
    {
        var options = new FixedOptionsMonitor<DocumentVaultOptions>(new DocumentVaultOptions());
        var vault = new DocumentVaultService(context, store, options, NullLogger<DocumentVaultService>.Instance);
        var content = new DocumentContentService(context, store, NullLogger<DocumentContentService>.Instance);
        var controller = new DocumentsController(
            vault,
            content,
            new DocumentRetentionService(context),
            NullLogger<DocumentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return controller;
    }
}
