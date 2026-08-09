using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Documents;

public sealed record DocumentContentMetadata(
    int Id,
    string StorageObjectPath,
    string ContentType,
    string FileName,
    string Sha256);

public sealed class DocumentContentService(
    AppDbContext context,
    IDocumentVaultStore store,
    ILogger<DocumentContentService> logger)
{
    public Task<DocumentContentMetadata?> GetMetadataAsync(int id, CancellationToken ct = default) =>
        context.VaultDocuments.AsNoTracking()
            .Where(document => document.Id == id)
            .Select(document => new DocumentContentMetadata(
                document.Id,
                document.StorageObjectPath,
                document.ContentType,
                document.OriginalFileName,
                document.Sha256))
            .FirstOrDefaultAsync(ct);

    public async Task<byte[]?> DownloadAsync(DocumentContentMetadata document, CancellationToken ct = default)
    {
        try
        {
            return await store.DownloadAsync(document.StorageObjectPath, ct);
        }
        catch (DocumentVaultStoreException ex)
        {
            logger.LogError(ex, "Failed to download GCS object {ObjectPath} for VaultDocument {Id}.", document.StorageObjectPath, document.Id);
            return null;
        }
    }
}
