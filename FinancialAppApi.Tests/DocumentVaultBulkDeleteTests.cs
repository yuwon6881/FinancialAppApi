using FinancialAppApi.Models;
using FinancialAppApi.Services.Documents;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public sealed class DocumentVaultBulkDeleteTests
{
    [Fact]
    public async Task DeleteManyAsync_SavesObjectsDeletedBeforeCancellationBeforePropagating()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.AppUsers.Add(new AppUser { Id = "test-user", Username = "test", PasswordHash = "hash" });
        context.VaultDocuments.AddRange(Document(1), Document(2));
        await context.SaveChangesAsync();

        using var cancellation = new CancellationTokenSource();
        var store = new CancelAfterFirstDeleteStore(cancellation);
        var service = new DocumentVaultService(
            context,
            store,
            new FixedOptionsMonitor<DocumentVaultOptions>(new DocumentVaultOptions()),
            NullLogger<DocumentVaultService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DeleteManyAsync([1, 2], cancellation.Token));

        // Cancellation is not permission to put back a storage deletion that already succeeded.
        Assert.Null(await context.VaultDocuments.FindAsync(1));
        Assert.NotNull(await context.VaultDocuments.FindAsync(2));
        Assert.Equal(1, store.SaveableDeleteCount);
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

    private sealed class CancelAfterFirstDeleteStore(CancellationTokenSource cancellation) : IDocumentVaultStore
    {
        private int _deleteCalls;

        public int SaveableDeleteCount { get; private set; }
        public bool IsConfigured => true;

        public Task UploadAsync(string objectPath, byte[] data, string contentType, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<byte[]?> DownloadAsync(string objectPath, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);

        public Task<bool> DownloadToAsync(string objectPath, Stream destination, CancellationToken ct = default) =>
            Task.FromResult(false);

        public async Task DeleteIfExistsAsync(string objectPath, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _deleteCalls) == 1)
            {
                SaveableDeleteCount++;
                cancellation.Cancel();
                await Task.Yield();
                return;
            }

            ct.ThrowIfCancellationRequested();
        }
    }
}
