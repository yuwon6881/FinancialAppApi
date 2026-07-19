using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

internal sealed class FakeReceiptImageStore : IReceiptImageStore
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    public bool FailUploads { get; set; }

    public bool FailDownloads { get; set; }

    public bool FailDeletes { get; set; }

    public Task UploadAsync(
        string objectPath,
        byte[] imageData,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailUploads) throw new ReceiptImageStoreException("Upload failed.");
        Objects.Add(objectPath, imageData.ToArray());
        return Task.CompletedTask;
    }

    public Task<byte[]?> DownloadAsync(
        string objectPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailDownloads) throw new ReceiptImageStoreException("Download failed.");
        return Task.FromResult(Objects.TryGetValue(objectPath, out var data) ? data.ToArray() : null);
    }

    public Task DeleteIfExistsAsync(
        string objectPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailDeletes) throw new ReceiptImageStoreException("Delete failed.");
        Objects.Remove(objectPath);
        return Task.CompletedTask;
    }
}
