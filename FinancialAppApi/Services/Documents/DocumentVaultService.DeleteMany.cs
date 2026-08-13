using Microsoft.EntityFrameworkCore;
using System.Runtime.ExceptionServices;

namespace FinancialAppApi.Services.Documents;

public sealed partial class DocumentVaultService
{
    public async Task<IReadOnlyList<(int Id, DocumentDeleteOutcome Outcome)>> DeleteManyAsync(
        IReadOnlyList<int> ids,
        CancellationToken ct = default)
    {
        var requested = ids.Distinct().ToList();
        var documents = await _context.VaultDocuments
            .Where(document => requested.Contains(document.Id))
            .ToListAsync(ct);
        var documentsById = documents.ToDictionary(document => document.Id);
        var outcomes = new Dictionary<int, DocumentDeleteOutcome>();
        using var concurrency = new SemaphoreSlim(4);

        Exception? failure = null;
        try
        {
            await Task.WhenAll(documents.Select(async document =>
            {
                await concurrency.WaitAsync(ct);
                try
                {
                    await _store.DeleteIfExistsAsync(document.StorageObjectPath, ct);
                    lock (outcomes) outcomes[document.Id] = DocumentDeleteOutcome.Deleted;
                }
                catch (DocumentVaultStoreException ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to delete GCS object {ObjectPath} for VaultDocument {Id}.",
                        document.StorageObjectPath,
                        document.Id);
                    lock (outcomes) outcomes[document.Id] = DocumentDeleteOutcome.StorageFailed;
                }
                finally
                {
                    concurrency.Release();
                }
            }));
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        var deleted = documents
            .Where(document => outcomes.TryGetValue(document.Id, out var outcome)
                && outcome == DocumentDeleteOutcome.Deleted)
            .ToList();
        if (deleted.Count > 0)
        {
            _context.VaultDocuments.RemoveRange(deleted);
            await _context.SaveChangesAsync(failure is null ? ct : CancellationToken.None);
        }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();

        return requested.Select(id => (
            id,
            documentsById.ContainsKey(id) ? outcomes[id] : DocumentDeleteOutcome.AlreadyGone)).ToList();
    }
}
