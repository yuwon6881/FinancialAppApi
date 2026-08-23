using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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

}
