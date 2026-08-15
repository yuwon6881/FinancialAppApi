using FinancialAppApi.Controllers;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Documents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public sealed class DocumentVaultZipAbortTests
{
    [Fact]
    public async Task Export_AbortsAfterAStoredObjectFailsMidStream()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.VaultDocuments.AddRange(Document(1), Document(2), Document(3));
        await context.SaveChangesAsync();

        var store = new ThirdObjectFailsStore();
        var service = new DocumentVaultService(
            context,
            store,
            new FixedOptionsMonitor<DocumentVaultOptions>(new DocumentVaultOptions()),
            NullLogger<DocumentVaultService>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Features.Set<IHttpRequestLifetimeFeature>(new AbortFeature());
        var controller = new DocumentsController(
            service,
            new DocumentContentService(context, store, NullLogger<DocumentContentService>.Instance),
            new DocumentRetentionService(context),
            NullLogger<DocumentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
        await using var output = new MemoryStream();
        controller.Response.Body = output;

        var result = await controller.Export(null, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(3, store.DownloadToCalls);
        Assert.True(controller.HttpContext.RequestAborted.IsCancellationRequested);
    }

    private static VaultDocument Document(int id) => new()
    {
        Id = id,
        UserId = "test-user",
        OriginalFileName = $"{id}.pdf",
        StorageObjectPath = $"test-user/2026/{id}.pdf",
        ContentType = "application/pdf",
        Sha256 = $"hash-{id}",
        SizeBytes = 1,
        TaxYear = 2026,
        UploadedAt = DateTime.UtcNow,
        RetentionUntil = new DateOnly(2033, 12, 31),
    };

    private sealed class ThirdObjectFailsStore : IDocumentVaultStore
    {
        public int DownloadToCalls { get; private set; }
        public bool IsConfigured => true;

        public Task UploadAsync(string objectPath, byte[] data, string contentType, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<byte[]?> DownloadAsync(string objectPath, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);

        public async Task<bool> DownloadToAsync(string objectPath, Stream destination, CancellationToken ct = default)
        {
            DownloadToCalls++;
            if (DownloadToCalls == 3)
                throw new DocumentVaultStoreException("stored object failed");

            await destination.WriteAsync("pdf"u8.ToArray(), ct);
            return true;
        }

        public Task DeleteIfExistsAsync(string objectPath, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class AbortFeature : IHttpRequestLifetimeFeature
    {
        private readonly CancellationTokenSource cancellation = new();

        public CancellationToken RequestAborted
        {
            get => cancellation.Token;
            set { }
        }

        public void Abort() => cancellation.Cancel();
    }
}
