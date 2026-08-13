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
    public int DownloadCalls { get; private set; }
    public HashSet<string> FailedDeletePaths { get; } = new(StringComparer.Ordinal);
    public int DeleteCalls { get; private set; }
    public int MaxConcurrentDeletes { get; private set; }
    private int _concurrentDeletes;

    public Task UploadAsync(string objectPath, byte[] data, string contentType, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (FailUploads) throw new DocumentVaultStoreException("Upload failed.");
        Objects[objectPath] = data.ToArray();
        return Task.CompletedTask;
    }

    public Task<byte[]?> DownloadAsync(string objectPath, CancellationToken ct = default)
    {
        DownloadCalls++;
        ct.ThrowIfCancellationRequested();
        if (FailDownloads) throw new DocumentVaultStoreException("Download failed.");
        return Task.FromResult(Objects.TryGetValue(objectPath, out var data) ? data.ToArray() : null);
    }

    public async Task<bool> DownloadToAsync(string objectPath, Stream destination, CancellationToken ct = default)
    {
        DownloadCalls++;
        ct.ThrowIfCancellationRequested();
        if (FailDownloads) throw new DocumentVaultStoreException("Download failed.");
        if (!Objects.TryGetValue(objectPath, out var data)) return false;
        await destination.WriteAsync(data, ct);
        return true;
    }

    public async Task DeleteIfExistsAsync(string objectPath, CancellationToken ct = default)
    {
        DeleteCalls++;
        var concurrent = Interlocked.Increment(ref _concurrentDeletes);
        MaxConcurrentDeletes = Math.Max(MaxConcurrentDeletes, concurrent);
        ct.ThrowIfCancellationRequested();
        try
        {
            await Task.Yield();
            if (FailDeletes || FailedDeletePaths.Contains(objectPath)) throw new DocumentVaultStoreException("Delete failed.");
            Objects.Remove(objectPath);
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentDeletes);
        }
    }
}

internal sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
