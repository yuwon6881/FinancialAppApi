using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;

namespace FinancialAppApi.Services.Documents;

public partial class DocumentVaultService
{
    public async Task WriteZipAsync(IReadOnlyCollection<int> ids, Stream output, CancellationToken ct = default)
    {
        await WriteZipAsync(ExportQuery(null, ids), output, ct);
    }

    private async Task WriteZipAsync(IQueryable<VaultDocument> query, Stream output, CancellationToken ct)
    {
        var documents = await query.OrderBy(document => document.TaxYear).ThenBy(document => document.Id).ToListAsync(ct);

        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var document in documents)
            {
                var safeName = Path.GetFileName(document.OriginalFileName);
                var entry = archive.CreateEntry($"{document.TaxYear}/{document.Id}-{safeName}", CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                if (!await _store.DownloadToAsync(document.StorageObjectPath, entryStream, ct))
                {
                    throw new DocumentVaultStoreException($"The stored document {document.Id} could not be found.");
                }
            }
        }
    }

    private IQueryable<VaultDocument> ExportQuery(int? taxYear, IReadOnlyCollection<int>? ids)
    {
        var query = _context.VaultDocuments.AsNoTracking().AsQueryable();
        if (taxYear.HasValue) query = query.Where(document => document.TaxYear == taxYear.Value);
        if (ids is { Count: > 0 }) query = query.Where(document => ids.Contains(document.Id));
        return query;
    }
}
