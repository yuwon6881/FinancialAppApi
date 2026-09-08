namespace FinancialAppApi.Services;

public interface IReceiptImageStore
{
    Task UploadAsync(
        string objectPath,
        byte[] imageData,
        string mimeType,
        CancellationToken cancellationToken = default);

    Task<byte[]?> DownloadAsync(
        string objectPath,
        CancellationToken cancellationToken = default);

    Task DeleteIfExistsAsync(
        string objectPath,
        CancellationToken cancellationToken = default);
}

public sealed class ReceiptImageStoreException : Exception
{
    public ReceiptImageStoreException(string message) : base(message)
    {
    }

    public ReceiptImageStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
