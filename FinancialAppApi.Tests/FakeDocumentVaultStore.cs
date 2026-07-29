using FinancialAppApi.Services.Documents;
using Microsoft.Extensions.Options;

namespace FinancialAppApi.Tests;

internal sealed class FakeDocumentVaultStore : IDocumentVaultStore
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    public bool FailUploads { get; set; }
    public bool FailDownloads { get; set; }
    public bool FailDeletes { get; set; }
    public bool IsConfigured { get; set; } = true;

    public Task UploadAsync(string objectPath, byte[] data, string contentType, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (FailUploads) throw new DocumentVaultStoreException("Upload failed.");
        Objects[objectPath] = data.ToArray();
        return Task.CompletedTask;
    }

    public Task<byte[]?> DownloadAsync(string objectPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (FailDownloads) throw new DocumentVaultStoreException("Download failed.");
        return Task.FromResult(Objects.TryGetValue(objectPath, out var data) ? data.ToArray() : null);
    }

    public Task DeleteIfExistsAsync(string objectPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (FailDeletes) throw new DocumentVaultStoreException("Delete failed.");
        Objects.Remove(objectPath);
        return Task.CompletedTask;
    }
}

internal sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
