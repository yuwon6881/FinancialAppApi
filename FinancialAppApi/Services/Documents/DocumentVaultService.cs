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

/// <summary>What a delete attempt actually did. Only <see cref="StorageFailed"/> is a failure.</summary>
public enum DocumentDeleteOutcome
{
    Deleted,
    AlreadyGone,
    StorageFailed
}

public enum TaxReliefCategoryMutationStatus
{
    Saved,
    Invalid,
    Duplicate,
    NotFound,
    InUse
}

public sealed record TaxReliefCategoryMutationResult(
    TaxReliefCategoryMutationStatus Status,
    TaxReliefCategoryDefinition? Category = null);

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
/// Manages the lifecycle of user-uploaded vault documents: upload, download, deletion, listing and
/// tax-relief category configuration. The two metadata write paths live in
/// <c>DocumentVaultService.Update.cs</c>; how long records are kept lives in
/// <see cref="DocumentRetentionService"/>.
/// </summary>
public sealed partial class DocumentVaultService
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

        if (!IsAllowedTaxYear(taxYear))
        {
            return new DocumentVaultCreateResult(DocumentVaultCreateStatus.InvalidMetadata, Message: GetTaxYearValidationMessage());
        }

        if (string.IsNullOrWhiteSpace(originalFileName) || originalFileName.Length > 255 ||
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
        if (mimeType == null || !AcceptedUploadTypes.Contains(mimeType))
        {
            return new DocumentVaultCreateResult(
                DocumentVaultCreateStatus.UnsupportedType,
                Message: "Upload a photo or a PDF. Other kinds of file cannot be kept as tax evidence.");
        }

        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileData))
            .ToLowerInvariant();

        var extraction = amountOverride.HasValue
            ? new VaultAmountExtraction(amountOverride.Value, normalizedAmountCurrency ?? "MYR", null, "Confirmed", null)
            : _amountExtractor == null
                ? new VaultAmountExtraction(null, "MYR", null, "Unavailable", "AI amount extraction is unavailable.")
                : await _amountExtractor.ExtractAsync(originalFileName, mimeType, fileData, ct);

        // INVARIANT: StorageObjectPath is an immutable opaque key. The tax year appears in it only
        // because it was convenient when the object was written, and re-filing a document to another
        // year deliberately does not move the object — so the prefix and VaultDocument.TaxYear
        // legitimately disagree from then on. Never parse a tax year out of this path, and never key
        // a storage-side lifecycle rule off the prefix; read the column instead. (The ZIP export
        // groups by the column, which is why its folders are right even when the prefix is not.)
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

        var retentionUntil = DocumentRetentionService.RetentionUntilFor(taxYear);

        var doc = new VaultDocument
        {
            UserId = userId,
            StorageObjectPath = objectPath,
            OriginalFileName = originalFileName,
            ContentType = mimeType,
            SizeBytes = fileData.LongLength,
            Sha256 = sha256,
            TaxYear = taxYear,
            ReliefCategory = reliefCategory,
            Amount = extraction.Amount,
            AmountCurrency = extraction.Currency,
            AmountStatus = extraction.Status,
            AmountConfidence = extraction.Confidence,
            AmountExtractionMessage = extraction.Message,
            TransactionId = transactionId,
            // Stays a UTC instant, deliberately: FinancialClock is for date-based financial behaviour
            // (which cycle, which tax year), and this is a point in time that is ordered and compared.
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

    /// <summary>
    /// Deleting a document, reporting which of the three things happened. <see cref="DocumentDeleteOutcome.AlreadyGone"/>
    /// is not an error — a replayed request must still succeed — but it is not a deletion either, and
    /// collapsing the two into `true` meant a bulk result counted rows nobody had just removed.
    /// </summary>
    public async Task<DocumentDeleteOutcome> DeleteAsync(int id, CancellationToken ct = default)
    {
        var doc = await _context.VaultDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc == null)
        {
            return DocumentDeleteOutcome.AlreadyGone;
        }

        try
        {
            await _store.DeleteIfExistsAsync(doc.StorageObjectPath, ct);
        }
        catch (DocumentVaultStoreException ex)
        {
            _logger.LogError(ex, "Failed to delete GCS object {ObjectPath} for VaultDocument {Id}.", doc.StorageObjectPath, doc.Id);
            return DocumentDeleteOutcome.StorageFailed;
        }

        _context.VaultDocuments.Remove(doc);
        await _context.SaveChangesAsync(ct);
        return DocumentDeleteOutcome.Deleted;
    }

    public async Task<(List<VaultDocumentDto> Items, int TotalCount)> ListAsync(
        int? taxYear,
        string? transactionId,
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

        if (!string.IsNullOrWhiteSpace(reliefCategory))
        {
            // The client sends one or more categories joined by commas so a single filter
            // (documents in category A OR category B) round-trips through one query param.
            var categories = reliefCategory
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);
            if (categories.Count > 0)
            {
                query = query.Where(d => d.ReliefCategory != null && categories.Contains(d.ReliefCategory));
            }
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

    public async Task<TaxYearReliefSummary?> GetTaxYearSummaryAsync(int taxYear, CancellationToken ct = default)
    {
        if (!IsAllowedTaxYear(taxYear)) return null;

        // Aggregated per category in SQL rather than by pulling every document for the year back and
        // summing in memory. Only Confirmed MYR amounts count towards a relief limit, which is why
        // the currency and status appear inside each sum rather than as a filter over the whole set.
        var perCategory = await _context.VaultDocuments
            .AsNoTracking()
            .Where(document => document.TaxYear == taxYear)
            .GroupBy(document => document.ReliefCategory)
            .Select(group => new
            {
                ReliefCategory = group.Key,
                DocumentCount = group.Count(),
                Confirmed = group.Sum(document =>
                    document.AmountStatus == "Confirmed" && document.AmountCurrency == "MYR"
                        ? document.Amount ?? 0
                        : 0),
                Pending = group.Sum(document =>
                    document.AmountStatus == "NeedsReview" && document.AmountCurrency == "MYR"
                        ? document.Amount ?? 0
                        : 0),
                PendingReviewCount = group.Count(document => document.AmountStatus == "NeedsReview"),
            })
            .ToListAsync(ct);

        var definitions = await GetReliefCategoriesAsync(taxYear, ct);
        if (perCategory.Count == 0 && definitions.Count == 0) return null;

        var totalsByCategory = perCategory
            .Where(entry => entry.ReliefCategory != null)
            .ToDictionary(entry => entry.ReliefCategory!, StringComparer.Ordinal);

        var summaries = definitions.Select(definition =>
        {
            totalsByCategory.TryGetValue(definition.Id, out var totals);
            return new TaxReliefCategorySummary(
                definition.Id,
                definition.Name,
                definition.Limit,
                totals?.Confirmed ?? 0,
                totals?.Pending ?? 0,
                totals?.DocumentCount ?? 0,
                totals?.PendingReviewCount ?? 0);
        }).ToList();

        return new TaxYearReliefSummary(
            taxYear,
            summaries.Sum(summary => summary.ConfirmedAmount),
            summaries.Sum(summary => summary.PendingReviewAmount),
            // Every document for the year, including any whose category is unset or no longer
            // configured — those are exactly the ones needing repair, so they must still be counted.
            perCategory.Sum(entry => entry.DocumentCount),
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

    /// <summary>The wording every caller uses when a tax year is out of range, so they cannot differ.</summary>
    public string TaxYearValidationMessage => GetTaxYearValidationMessage();

    public async Task<TaxReliefCategoryMutationResult> AddReliefCategoryAsync(
        int taxYear,
        string? name,
        decimal limit,
        CancellationToken ct = default)
    {
        if (!TryNormalizeCategoryInput(name, limit, out var normalizedName))
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
            TaxYear = taxYear
        };
        _context.TaxReliefCategoryLimits.Add(category);
        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The duplicate check above reads rows loaded before this insert, so two adds of the same
            // name race to the same generated CategoryId and one of them loses on the unique index.
            // That is the same conflict the check reports, and it must read the same way rather than
            // escaping as a 500.
            _context.Entry(category).State = EntityState.Detached;
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.Duplicate);
        }

        return new TaxReliefCategoryMutationResult(
            TaxReliefCategoryMutationStatus.Saved,
            ToDefinition(category));
    }

    public async Task<TaxReliefCategoryMutationResult> UpdateReliefCategoryAsync(
        int taxYear,
        string categoryId,
        string? name,
        decimal limit,
        CancellationToken ct = default)
    {
        if (!TryNormalizeCategoryInput(name, limit, out var normalizedName) ||
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
        await _context.SaveChangesAsync(ct);

        return new TaxReliefCategoryMutationResult(
            TaxReliefCategoryMutationStatus.Saved,
            ToDefinition(category));
    }

    /// <summary>
    /// Removes a relief category from one tax year. Documents carry a required relief category,
    /// so a category that is still referenced by a document in that year is refused rather than
    /// leaving those documents pointing at a category that no longer exists.
    /// </summary>
    public async Task<TaxReliefCategoryMutationResult> DeleteReliefCategoryAsync(
        int taxYear,
        string categoryId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(categoryId) || categoryId.Length > 80 || !IsAllowedTaxYear(taxYear))
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.Invalid);
        }

        var rows = await EnsureExplicitConfigurationAsync(taxYear, ct);
        var category = rows.FirstOrDefault(row => row.CategoryId == categoryId);
        if (category == null)
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.NotFound);
        }

        var isInUse = await _context.VaultDocuments
            .AnyAsync(document => document.TaxYear == taxYear && document.ReliefCategory == categoryId, ct);
        if (isInUse)
        {
            return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.InUse);
        }

        _context.TaxReliefCategoryLimits.Remove(category);
        await _context.SaveChangesAsync(ct);

        return new TaxReliefCategoryMutationResult(TaxReliefCategoryMutationStatus.Saved);
    }

    public Task<bool> HasDocumentsForExportAsync(int? taxYear, CancellationToken ct = default)
    {
        return ExportQuery(taxYear, null).AnyAsync(ct);
    }

    public async Task WriteZipAsync(int? taxYear, Stream output, CancellationToken ct = default)
    {
        await WriteZipAsync(ExportQuery(taxYear, null), output, ct);
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

    // How far back a new upload may be filed. It is the same span records are worth keeping for, so
    // it reads from one place: a year that can still be filed is a year still worth keeping.
    private const int TaxYearLookbackYears = DocumentRetentionService.KeepYears;

    /// <summary>
    /// What a tax vault will accept: a photo of a receipt, or a PDF. `FileSignatureInspector` also
    /// recognises XML and JSON, and those are deliberately excluded here — a tax record is never one
    /// of them, and they are the only detected types a browser will both render and execute, which
    /// matters because <c>/documents/{id}/content</c> is served from the origin holding the auth
    /// cookie. Existing rows of those types stay listed and downloadable; only new uploads are refused.
    /// </summary>
    private static readonly HashSet<string> AcceptedUploadTypes = new(StringComparer.Ordinal)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/heic",
        "image/heif",
        "application/pdf",
    };

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
        new(row.CategoryId, row.Name, row.Limit, isInherited);

    private static bool TryNormalizeCategoryInput(
        string? name,
        decimal limit,
        out string normalizedName)
    {
        normalizedName = name?.Trim() ?? string.Empty;
        return normalizedName.Length is > 0 and <= 120 &&
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
        return taxYear >= currentYear - TaxYearLookbackYears && taxYear <= currentYear;
    }

    private string GetTaxYearValidationMessage()
    {
        var currentYear = _financialClock.Today.Year;
        return $"Tax year must be between {currentYear - TaxYearLookbackYears} and {currentYear}.";
    }
}
