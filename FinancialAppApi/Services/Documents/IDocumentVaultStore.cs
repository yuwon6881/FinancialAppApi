

namespace FinancialAppApi.Services.Documents;

/// <summary>
/// Stores and retrieves vault documents in Google Cloud Storage.
/// </summary>
public interface IDocumentVaultStore
{
    Task UploadAsync(string objectPath, byte[] data, string contentType, CancellationToken ct = default);
    Task<byte[]?> DownloadAsync(string objectPath, CancellationToken ct = default);
    Task<bool> DownloadToAsync(string objectPath, Stream destination, CancellationToken ct = default);
    Task DeleteIfExistsAsync(string objectPath, CancellationToken ct = default);
    bool IsConfigured { get; }
}

/// <summary>
/// Thrown when a vault document storage operation fails.
/// </summary>
public sealed class DocumentVaultStoreException : System.Exception
{
    public DocumentVaultStoreException(string message) : base(message)
    {
    }

    public DocumentVaultStoreException(string message, System.Exception innerException)
        : base(message, innerException)
    {
    }
}
