using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using System.Text;

namespace FinancialAppApi.Services.Documents;

/// <summary>
/// A read-only projection of a vault document for API responses.
/// </summary>
public sealed record VaultDocumentDto(
    int Id,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    int TaxYear,
    string? Notes,
    string? ReliefCategory,
    decimal? Amount,
    string AmountCurrency,
    string AmountStatus,
    decimal? AmountConfidence,
    string? AmountExtractionMessage,
    string? TransactionId,
    DateTime UploadedAt,
    DateOnly RetentionUntil);

/// <summary>
/// Per-user storage usage summary for the document vault. <paramref name="QuotaBytes"/> is echoed
/// from configuration so the client's usage meter reports the real limit rather than assuming one.
/// </summary>
public sealed record DocumentVaultUsage(long TotalBytes, int DocumentCount, long QuotaBytes);
public sealed record DocumentVaultConstraints(long MaxDocumentBytes, int MaxBulkDocuments, long MaxTotalBytesPerUser);
public sealed record TaxReliefCategoryDefinition(
    string Id,
    string Name,
    decimal Limit,
    string Detail,
    bool IsInherited = false);
public sealed record TaxReliefCategorySummary(
    string Id,
    string Name,
    decimal Limit,
    decimal ConfirmedAmount,
    decimal PendingReviewAmount,
    int DocumentCount,
    int PendingReviewCount);
public sealed record TaxYearReliefSummary(
    int TaxYear,
    decimal ConfirmedAmount,
    decimal PendingReviewAmount,
    int DocumentCount,
    IReadOnlyList<TaxReliefCategorySummary> Categories);
public sealed record ExpiredTaxYearSummary(int TaxYear, int DocumentCount, long TotalBytes, DateOnly RetentionUntil);

public enum TaxReliefCategoryMutationStatus
{
    Saved,
    Invalid,
    Duplicate,
    NotFound
}

public sealed record TaxReliefCategoryMutationResult(
    TaxReliefCategoryMutationStatus Status,
    TaxReliefCategoryDefinition? Category = null);

public sealed record ReliefCategoryDocumentUpdate(int Id, string? ReliefCategory);
public sealed record ReliefCategoryDocumentUpdateResult(int Id, bool Updated, string? Message = null);


/// <summary>
/// Outcome codes for a vault document upload attempt.
/// </summary>
public enum DocumentVaultCreateStatus
{
    Created,
    InvalidMetadata,
    FileTooLarge,
    QuotaExceeded,
    UnsupportedType,
    StorageUnavailable
}

/// <summary>
/// The result of a vault document upload attempt.
/// </summary>
public sealed record DocumentVaultCreateResult(
    DocumentVaultCreateStatus Status,
    int? DocumentId = null,
    string? Message = null);

/// <summary>
/// Manages the lifecycle of user-uploaded vault documents, including upload, metadata updates, download, and deletion.
/// </summary>
public sealed class DocumentVaultService
{
    private readonly AppDbContext _context;
    private readonly IDocumentVaultStore _store;
    private readonly IOptionsMonitor<DocumentVaultOptions> _options;
    private readonly ILogger<DocumentVaultService> _logger;
    private readonly FinancialClock _financialClock;
    private readonly VaultAmountExtractor? _amountExtractor;

    public DocumentVaultService(
        AppDbContext context,
        IDocumentVaultStore store,
        IOptionsMonitor<DocumentVaultOptions> options,
        ILogger<DocumentVaultService> logger,
        FinancialClock? financialClock = null,
        VaultAmountExtractor? amountExtractor = null)
    {
        _context = context;
        _store = store;
        _options = options;
        _logger = logger;
        _financialClock = financialClock ?? FinancialClock.Utc;
        _amountExtractor = amountExtractor;
    }

    public async Task<DocumentVaultCreateResult> CreateAsync(
        string originalFileName,
        byte[] fileData,
        int taxYear,
        string? notes,
        string? transactionId,
        string? clientKey,
        string? reliefCategory = null,
        CancellationToken ct = default,
        decimal? amountOverride = null,
        string? amountCurrencyOverride = null)
    {
        var options = _options.CurrentValue;
        reliefCategory = string.IsNullOrWhiteSpace(reliefCategory) ? null : reliefCategory.Trim();
        var normalizedAmountCurrency = string.IsNullOrWhiteSpace(amountCurrencyOverride)
            ? null
            : amountCurrencyOverride.Trim().ToUpperInvariant();

        if (!_store.IsConfigured)
        {
            return new DocumentVaultCreateResult(DocumentVaultCreateStatus.StorageUnavailable, Message: "Document storage is not configured.");
        }

        if (reliefCategory == null)
        {
            return new DocumentVaultCreateResult(DocumentVaultCreateStatus.InvalidMetadata, Message: "A tax relief category is required.");
        }

        if (string.IsNullOrWhiteSpace(originalFileName) || originalFileName.Length > 255 ||
            !IsAllowedTaxYear(taxYear) ||
            notes?.Length > 500 ||
            transactionId?.Length > 450 ||
            clientKey?.Length > 64 ||
            reliefCategory.Length > 80 ||
            amountOverride is < 0 ||
            (amountOverride.HasValue && normalizedAmountCurrency is not ("MYR" or "OTHER")) ||
            !await IsReliefCategoryConfiguredAsync(taxYear, reliefCategory, ct))
        {
            return new DocumentVaultCreateResult(DocumentVaultCreateStatus.InvalidMetadata, Message: "Document metadata is invalid.");
        }

        if (fileData.LongLength > options.MaxDocumentBytes)
        {
            return new DocumentVaultCreateResult(DocumentVaultCreateStatus.FileTooLarge, Message: "The document exceeds the maximum allowed size.");
        }

        var userId = _context.RequireCurrentUserId();
        if (!string.IsNullOrWhiteSpace(clientKey))
        {
            var existingId = await _context.VaultDocuments
                .Where(document => document.ClientKey == clientKey)
                .Select(document => (int?)document.Id)
                .FirstOrDefaultAsync(ct);
            if (existingId.HasValue)
            {
                return new DocumentVaultCreateResult(DocumentVaultCreateStatus.Created, existingId.Value);
            }
        }

        var currentTotalBytes = await _context.VaultDocuments
            .SumAsync(d => d.SizeBytes, ct);

        if (currentTotalBytes + fileData.LongLength > options.MaxTotalBytesPerUser)
        {
            return new DocumentVaultCreateResult(DocumentVaultCreateStatus.QuotaExceeded, Message: "Storage quota exceeded.");
        }

        var mimeType = FileSignatureInspector.DetectMimeType(fileData);
        if (mimeType == null)
        {
            return new DocumentVaultCreateResult(DocumentVaultCreateStatus.UnsupportedType, Message: "Unsupported file type.");
        }

        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileData))
            .ToLowerInvariant();

        var extraction = amountOverride.HasValue
            ? new VaultAmountExtraction(amountOverride.Value, normalizedAmountCurrency ?? "MYR", null, "Confirmed", null)
            : _amountExtractor == null
                ? new VaultAmountExtraction(null, "MYR", null, "Unavailable", "AI amount extraction is unavailable.")
                : await _amountExtractor.ExtractAsync(originalFileName, mimeType, fileData, ct);

        var documentGuid = Guid.NewGuid().ToString("N");
        var ext = FileSignatureInspector.ExtensionForMimeType(mimeType);
        var objectPath = $"{userId}/{taxYear}/{documentGuid}{ext}";

        try
        {
            await _store.UploadAsync(objectPath, fileData, mimeType, ct);
        }
        catch (DocumentVaultStoreException ex)
        {
            _logger.LogError(ex, "Could not upload document {ObjectPath} to GCS.", objectPath);
            return new DocumentVaultCreateResult(DocumentVaultCreateStatus.StorageUnavailable, Message: "Document storage is temporarily unavailable.");
        }

        var retentionUntil = new DateOnly(taxYear, 12, 31).AddYears(7);

        var doc = new VaultDocument
        {
            UserId = userId,
            StorageObjectPath = objectPath,
            OriginalFileName = originalFileName,
            ContentType = mimeType,
            SizeBytes = fileData.LongLength,
            Sha256 = sha256,
            TaxYear = taxYear,
            Notes = notes,
            ReliefCategory = reliefCategory,
            Amount = extraction.Amount,
            AmountCurrency = extraction.Currency,
            AmountStatus = extraction.Status,
            AmountConfidence = extraction.Confidence,
            AmountExtractionMessage = extraction.Message,
            TransactionId = transactionId,
            UploadedAt = DateTime.UtcNow,
            RetentionUntil = retentionUntil,
            ClientKey = clientKey
        };

        _context.VaultDocuments.Add(doc);

        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch
        {
            try
            {
                await _store.DeleteIfExistsAsync(objectPath, ct);
            }
            catch (DocumentVaultStoreException cleanupEx)
            {
                _logger.LogError(cleanupEx, "Failed to rollback GCS object {ObjectPath} after DB insert failed.", objectPath);
            }
            throw;
        }

        return new DocumentVaultCreateResult(DocumentVaultCreateStatus.Created, doc.Id);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        var doc = await _context.VaultDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc == null)
        {
            return true;
        }

        try
        {
            await _store.DeleteIfExistsAsync(doc.StorageObjectPath, ct);
        }
        catch (DocumentVaultStoreException ex)
        {
            _logger.LogError(ex, "Failed to delete GCS object {ObjectPath} for VaultDocument {Id}.", doc.StorageObjectPath, doc.Id);
            return false;
        }

        _context.VaultDocuments.Remove(doc);
        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(List<VaultDocumentDto> Items, int TotalCount)> ListAsync(
        int? taxYear,
        string? transactionId,
        string? search,
        string? reliefCategory,
        string? sort,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        var query = _context.VaultDocuments.AsNoTracking().AsQueryable();

        if (taxYear.HasValue)
        {
            query = query.Where(d => d.TaxYear == taxYear.Value);
        }

        if (!string.IsNullOrWhiteSpace(transactionId))
        {
            query = query.Where(d => d.TransactionId == transactionId);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLower();
            query = query.Where(d => d.OriginalFileName.ToLower().Contains(s) || (d.Notes != null && d.Notes.ToLower().Contains(s)));
        }

        if (!string.IsNullOrWhiteSpace(reliefCategory))
        {
            var category = reliefCategory.Trim();
            query = query.Where(d => d.ReliefCategory == category);
        }

        var totalCount = await query.CountAsync(ct);

        var normalizedSort = sort?.Trim().ToLowerInvariant();
        IOrderedQueryable<VaultDocument> orderedQuery = normalizedSort switch
        {
            "uploaded-asc" => query.OrderBy(d => d.UploadedAt).ThenBy(d => d.Id),
            "name-asc" => query.OrderBy(d => d.OriginalFileName).ThenByDescending(d => d.UploadedAt).ThenByDescending(d => d.Id),
            "name-desc" => query.OrderByDescending(d => d.OriginalFileName).ThenByDescending(d => d.UploadedAt).ThenByDescending(d => d.Id),
            "amount-desc" => query.OrderByDescending(d => d.Amount ?? 0m).ThenByDescending(d => d.UploadedAt).ThenByDescending(d => d.Id),
            "amount-asc" => query.OrderBy(d => d.Amount ?? 0m).ThenByDescending(d => d.UploadedAt).ThenByDescending(d => d.Id),
            _ => query.OrderByDescending(d => d.UploadedAt).ThenByDescending(d => d.Id),
        };

        var items = await orderedQuery
            .Skip(skip)
            .Take(take)
            .Select(d => new VaultDocumentDto(
                d.Id,
                d.OriginalFileName,
                d.ContentType,
                d.SizeBytes,
                d.TaxYear,
                d.Notes,
                d.ReliefCategory,
                d.Amount,
                d.AmountCurrency,
                d.AmountStatus,
                d.AmountConfidence,
                d.AmountExtractionMessage,
                d.TransactionId,
                d.UploadedAt,
                d.RetentionUntil))
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public Task<List<int>> GetAvailableTaxYearsAsync(CancellationToken ct = default)
    {
        // The Vault filter should only offer years that can return a document. Keeping this as a
        // distinct database projection avoids transferring every configurable year or making the
        // client infer availability from a paged document response.
        return _context.VaultDocuments
            .AsNoTracking()
            .Select(document => document.TaxYear)
            .Distinct()
            .OrderByDescending(year => year)
            .ToListAsync(ct);
    }

    public async Task<(byte[] Data, string ContentType, string FileName)?> GetContentAsync(
        int id,
        CancellationToken ct = default)
    {
        var doc = await _context.VaultDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc == null) return null;

        try
        {
            var data = await _store.DownloadAsync(doc.StorageObjectPath, ct);
            if (data == null) return null;
            return (data, doc.ContentType, doc.OriginalFileName);
        }
        catch (DocumentVaultStoreException ex)
        {
            _logger.LogError(ex, "Failed to download GCS object {ObjectPath} for VaultDocument {Id}.", doc.StorageObjectPath, doc.Id);
            return null;
        }
    }

    public async Task<VaultDocumentDto?> UpdateAsync(
        int id,
        int? taxYear,
        string? notes,
        bool updateNotes,
        string? transactionId,
        bool updateTransactionId,
        string? reliefCategory,
        bool updateReliefCategory,
        decimal? amount,
        bool updateAmount,
        string? amountCurrency,
        string? amountStatus,
        CancellationToken ct = default)
    {
        var doc = await _context.VaultDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc == null) return null;

        if (taxYear.HasValue && !IsAllowedTaxYear(taxYear.Value))
        {
            return null;
        }
        reliefCategory = string.IsNullOrWhiteSpace(reliefCategory) ? null : reliefCategory.Trim();
        var targetTaxYear = taxYear ?? doc.TaxYear;
        if (updateReliefCategory &&
            (reliefCategory == null || !await IsReliefCategoryConfiguredAsync(targetTaxYear, reliefCategory, ct)))
        {
            return null;
        }
        if (updateAmount && amount is < 0)
        {
            return null;
        }
        if (amountCurrency != null && amountCurrency is not ("MYR" or "OTHER"))
        {
            return null;
        }
        if (amountStatus != null && amountStatus is not ("Confirmed" or "NeedsReview"))
        {
            return null;
        }
        if (taxYear.HasValue && taxYear.Value != doc.TaxYear)
        {
            doc.TaxYear = taxYear.Value;
            doc.RetentionUntil = new DateOnly(taxYear.Value, 12, 31).AddYears(7);
        }
        if (updateNotes)
        {
            doc.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes;
        }
        if (updateTransactionId)
        {
            doc.TransactionId = string.IsNullOrWhiteSpace(transactionId) ? null : transactionId;
        }
        if (updateReliefCategory)
        {
            doc.ReliefCategory = reliefCategory;
        }
        if (updateAmount)
        {
            doc.Amount = amount;
            doc.AmountCurrency = amountCurrency ?? doc.AmountCurrency;
            doc.AmountStatus = amount.HasValue ? amountStatus ?? "Confirmed" : "NotFound";
            doc.AmountConfidence = amountStatus == "Confirmed" ? null : doc.AmountConfidence;
            doc.AmountExtractionMessage = null;
        }

        await _context.SaveChangesAsync(ct);

        return new VaultDocumentDto(
            doc.Id,
            doc.OriginalFileName,
            doc.ContentType,
            doc.SizeBytes,
            doc.TaxYear,
            doc.Notes,
            doc.ReliefCategory,
            doc.Amount,
            doc.AmountCurrency,
            doc.AmountStatus,
            doc.AmountConfidence,
            doc.AmountExtractionMessage,
            doc.TransactionId,
            doc.UploadedAt,
            doc.RetentionUntil);
    }

    public async Task<IReadOnlyList<ReliefCategoryDocumentUpdateResult>> UpdateReliefCategoriesAsync(
        IReadOnlyCollection<ReliefCategoryDocumentUpdate> updates,
        CancellationToken ct = default)
    {
        var requested = updates
            .GroupBy(update => update.Id)
            .Select(group => group.Last())
            .ToList();

        if (requested.Count == 0) return [];

        var ids = requested
            .Where(update => update.Id > 0)
            .Select(update => update.Id)
            .ToArray();
        var documents = await _context.VaultDocuments
            .Where(document => ids.Contains(document.Id))
            .ToListAsync(ct);
        var documentsById = documents.ToDictionary(document => document.Id);
        var validCategoriesByTaxYear = new Dictionary<int, HashSet<string>>();
        var results = new List<ReliefCategoryDocumentUpdateResult>(requested.Count);
        var hasChanges = false;

        foreach (var update in requested)
        {
            if (!documentsById.TryGetValue(update.Id, out var document))
            {
                results.Add(new(update.Id, false, "The document was not found."));
                continue;
            }

            var reliefCategory = update.ReliefCategory?.Trim();
            if (string.IsNullOrWhiteSpace(reliefCategory) || reliefCategory.Length > 80)
            {
                results.Add(new(update.Id, false, "A valid tax relief category is required."));
                continue;
            }

            if (!validCategoriesByTaxYear.TryGetValue(document.TaxYear, out var validCategories))
            {
                var (rows, _) = await GetEffectiveCategoryRowsAsync(document.TaxYear, ct);
                validCategories = rows
                    .Select(row => row.CategoryId)
                    .ToHashSet(StringComparer.Ordinal);
                validCategoriesByTaxYear[document.TaxYear] = validCategories;
            }

            if (!validCategories.Contains(reliefCategory))
            {
                results.Add(new(update.Id, false, "The category is not configured for this document's tax year."));
                continue;
            }

            if (!string.Equals(document.ReliefCategory, reliefCategory, StringComparison.Ordinal))
            {
                document.ReliefCategory = reliefCategory;
                hasChanges = true;
            }

            results.Add(new(update.Id, true));
        }

        if (hasChanges)
        {
            await _context.SaveChangesAsync(ct);
        }

        return results;
    }

    public async Task<DocumentVaultUsage> GetUsageAsync(CancellationToken ct = default)
    {
        var aggregate = await _context.VaultDocuments
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(group => new { TotalBytes = group.Sum(document => document.SizeBytes), Count = group.Count() })
            .FirstOrDefaultAsync(ct);

        return new DocumentVaultUsage(
            aggregate?.TotalBytes ?? 0,
            aggregate?.Count ?? 0,
            _options.CurrentValue.MaxTotalBytesPerUser);
    }

    public DocumentVaultConstraints GetConstraints()
    {
        var options = _options.CurrentValue;
        return new(options.MaxDocumentBytes, options.MaxBulkDocuments, options.MaxTotalBytesPerUser);
    }

    public async Task<List<ExpiredTaxYearSummary>> GetExpiredTaxYearsAsync(CancellationToken ct = default)
    {
        var today = _financialClock.Today;
        var expired = await _context.VaultDocuments
            .AsNoTracking()
            .Where(document => document.RetentionUntil < today)
            .Select(document => new { document.TaxYear, document.SizeBytes, document.RetentionUntil })
            .ToListAsync(ct);
        return expired
            .GroupBy(document => new { document.TaxYear, document.RetentionUntil })
            .Select(group => new ExpiredTaxYearSummary(
                group.Key.TaxYear,
                group.Count(),
                group.Sum(document => document.SizeBytes),
                group.Key.RetentionUntil))
            .OrderBy(summary => summary.TaxYear)
            .ToList();
    }

    public async Task<TaxYearReliefSummary?> GetTaxYearSummaryAsync(int taxYear, CancellationToken ct = default)
    {
        if (!IsAllowedTaxYear(taxYear)) return null;

        var documents = await _context.VaultDocuments
            .AsNoTracking()
            .Where(document => document.TaxYear == taxYear)
            .Select(document => new
            {
                document.ReliefCategory,
                document.Amount,
                document.AmountCurrency,
                document.AmountStatus
            })
            .ToListAsync(ct);
        var definitions = await GetReliefCategoriesAsync(taxYear, ct);
        if (documents.Count == 0 && definitions.Count == 0) return null;

        var summaries = definitions.Select(definition =>
        {
            var categoryDocuments = documents
                .Where(document => document.ReliefCategory == definition.Id)
                .ToList();
            var confirmed = categoryDocuments
                .Where(document => document.AmountStatus == "Confirmed" && document.AmountCurrency == "MYR")
                .Sum(document => document.Amount ?? 0);
            var pending = categoryDocuments
                .Where(document => document.AmountStatus == "NeedsReview" && document.AmountCurrency == "MYR")
                .Sum(document => document.Amount ?? 0);
            return new TaxReliefCategorySummary(
                definition.Id,
                definition.Name,
                definition.Limit,
                confirmed,
                pending,
                categoryDocuments.Count,
                categoryDocuments.Count(document => document.AmountStatus == "NeedsReview"));
        }).ToList();

        return new TaxYearReliefSummary(
            taxYear,
            summaries.Sum(summary => summary.ConfirmedAmount),
            summaries.Sum(summary => summary.PendingReviewAmount),
            documents.Count,
            summaries);
    }

    public async Task<IReadOnlyList<TaxReliefCategoryDefinition>> GetReliefCategoriesAsync(
        int taxYear,
        CancellationToken ct = default)
    {
        if (!IsAllowedTaxYear(taxYear)) return [];

        var (rows, isInherited) = await GetEffectiveCategoryRowsAsync(taxYear, ct);
        return rows
            .Select(row => ToDefinition(row, isInherited))
            .ToList();
    }

    public bool IsTaxYearAllowed(int taxYear) => IsAllowedTaxYear(taxYear);

    public async Task<TaxReliefCategoryMutationResult> AddReliefCategoryAsync(
        int taxYear,
        string? name,
        decimal limit,
        string? detail,
        CancellationToken ct = default)
    {
        if (!TryNormalizeCategoryInput(name, limit, detail, out var normalizedName, out var normalizedDetail))
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.Invalid);
        }
        if (!IsAllowedTaxYear(taxYear))
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.Invalid);
        }

        var rows = await EnsureExplicitConfigurationAsync(taxYear, ct);
        if (rows.Any(row => string.Equals(row.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.Duplicate);
        }

        var category = new TaxReliefCategoryLimit
        {
            CategoryId = CreateCategoryId(normalizedName, rows),
            Name = normalizedName,
            Limit = limit,
            Detail = normalizedDetail,
            TaxYear = taxYear
        };
        _context.TaxReliefCategoryLimits.Add(category);
        await _context.SaveChangesAsync(ct);

        return new TaxReliefCategoryMutationResult(
            TaxReliefCategoryMutationStatus.Saved,
            ToDefinition(category));
    }

    public async Task<TaxReliefCategoryMutationResult> UpdateReliefCategoryAsync(
        int taxYear,
        string categoryId,
        string? name,
        decimal limit,
        string? detail,
        CancellationToken ct = default)
    {
        if (!TryNormalizeCategoryInput(name, limit, detail, out var normalizedName, out var normalizedDetail) ||
            string.IsNullOrWhiteSpace(categoryId) || categoryId.Length > 80 ||
            !IsAllowedTaxYear(taxYear))
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.Invalid);
        }

        var rows = await EnsureExplicitConfigurationAsync(taxYear, ct);
        var category = rows.FirstOrDefault(row => row.CategoryId == categoryId);
        if (category == null)
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.NotFound);
        }
        if (rows.Any(row => row.Id != category.Id &&
                           string.Equals(row.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.Duplicate);
        }

        category.Name = normalizedName;
        category.Limit = limit;
        category.Detail = normalizedDetail;
        await _context.SaveChangesAsync(ct);

        return new TaxReliefCategoryMutationResult(
            TaxReliefCategoryMutationStatus.Saved,
            ToDefinition(category));
    }

    public Task<bool> HasDocumentsForExportAsync(int? taxYear, CancellationToken ct = default)
    {
        return ExportQuery(taxYear, null).AnyAsync(ct);
    }

    public async Task WriteZipAsync(int? taxYear, Stream output, CancellationToken ct = default)
    {
        await WriteZipAsync(ExportQuery(taxYear, null), output, ct);
    }

    public Task<bool> HasDocumentsForExportAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default)
    {
        return ExportQuery(null, ids).AnyAsync(ct);
    }

    public async Task<bool> HasAllDocumentsForExportAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default)
    {
        var distinctIds = ids.Distinct().ToArray();
        return distinctIds.Length > 0 && await ExportQuery(null, distinctIds).CountAsync(ct) == distinctIds.Length;
    }

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
                var data = await _store.DownloadAsync(document.StorageObjectPath, ct);
                if (data == null)
                {
                    throw new DocumentVaultStoreException($"The stored document {document.Id} could not be found.");
                }
                var safeName = Path.GetFileName(document.OriginalFileName);
                var entry = archive.CreateEntry($"{document.TaxYear}/{document.Id}-{safeName}", CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(data, ct);
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

    private const int MinTaxYear = 2000;

    private async Task<bool> IsReliefCategoryConfiguredAsync(
        int taxYear,
        string categoryId,
        CancellationToken ct)
    {
        var (rows, _) = await GetEffectiveCategoryRowsAsync(taxYear, ct);
        return rows.Any(row => row.CategoryId == categoryId);
    }

    private async Task<(List<TaxReliefCategoryLimit> Rows, bool IsInherited)> GetEffectiveCategoryRowsAsync(
        int taxYear,
        CancellationToken ct)
    {
        var current = await _context.TaxReliefCategoryLimits
            .AsNoTracking()
            .Where(row => row.TaxYear == taxYear)
            .OrderBy(row => row.Id)
            .ToListAsync(ct);
        if (current.Count > 0)
        {
            return (current, false);
        }

        var previousYear = await _context.TaxReliefCategoryLimits
            .AsNoTracking()
            .Where(row => row.TaxYear < taxYear)
            .OrderByDescending(row => row.TaxYear)
            .Select(row => (int?)row.TaxYear)
            .FirstOrDefaultAsync(ct);
        if (!previousYear.HasValue)
        {
            return ([], true);
        }

        var inherited = await _context.TaxReliefCategoryLimits
            .AsNoTracking()
            .Where(row => row.TaxYear == previousYear.Value)
            .OrderBy(row => row.Id)
            .ToListAsync(ct);
        return (inherited, true);
    }

    private async Task<List<TaxReliefCategoryLimit>> EnsureExplicitConfigurationAsync(
        int taxYear,
        CancellationToken ct)
    {
        var current = await _context.TaxReliefCategoryLimits
            .Where(row => row.TaxYear == taxYear)
            .OrderBy(row => row.Id)
            .ToListAsync(ct);
        if (current.Count > 0)
        {
            return current;
        }

        var (inherited, _) = await GetEffectiveCategoryRowsAsync(taxYear, ct);
        foreach (var row in inherited)
        {
            _context.TaxReliefCategoryLimits.Add(new TaxReliefCategoryLimit
            {
                CategoryId = row.CategoryId,
                Name = row.Name,
                Limit = row.Limit,
                Detail = row.Detail,
                TaxYear = taxYear
            });
        }

        if (inherited.Count > 0)
        {
            await _context.SaveChangesAsync(ct);
        }

        return await _context.TaxReliefCategoryLimits
            .Where(row => row.TaxYear == taxYear)
            .OrderBy(row => row.Id)
            .ToListAsync(ct);
    }

    private static TaxReliefCategoryDefinition ToDefinition(
        TaxReliefCategoryLimit row,
        bool isInherited = false) =>
        new(row.CategoryId, row.Name, row.Limit, row.Detail, isInherited);

    private static bool TryNormalizeCategoryInput(
        string? name,
        decimal limit,
        string? detail,
        out string normalizedName,
        out string normalizedDetail)
    {
        normalizedName = name?.Trim() ?? string.Empty;
        normalizedDetail = detail?.Trim() ?? string.Empty;
        return normalizedName.Length is > 0 and <= 120 &&
               normalizedDetail.Length <= 300 &&
               limit is >= 0 and <= 9_999_999_999_999_999.99m;
    }

    private static string CreateCategoryId(
        string name,
        IReadOnlyCollection<TaxReliefCategoryLimit> existingRows)
    {
        var builder = new StringBuilder();
        foreach (var character in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var baseId = builder.ToString().Trim('-');
        if (baseId.Length == 0) baseId = "category";
        if (baseId.Length > 60) baseId = baseId[..60].TrimEnd('-');

        var candidate = baseId;
        var suffix = 2;
        while (existingRows.Any(row => string.Equals(row.CategoryId, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            var suffixText = $"-{suffix++}";
            candidate = baseId[..Math.Min(baseId.Length, 80 - suffixText.Length)] + suffixText;
        }

        return candidate;
    }

    private bool IsAllowedTaxYear(int taxYear)
    {
        var currentYear = _financialClock.Today.Year;
        return taxYear >= MinTaxYear && taxYear <= currentYear;
    }
}
