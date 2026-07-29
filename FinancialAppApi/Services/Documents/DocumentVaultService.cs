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
    string DocumentType,
    string? Notes,
    string? TransactionId,
    DateTime UploadedAt,
    DateOnly RetentionUntil);

/// <summary>
/// Per-user storage usage summary for the document vault. <paramref name="QuotaBytes"/> is echoed
/// from configuration so the client's usage meter reports the real limit rather than assuming one.
/// </summary>
public sealed record DocumentVaultUsage(long TotalBytes, int DocumentCount, long QuotaBytes);


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
    private readonly VaultDocumentTypeService? _documentTypes;
    private readonly FinancialClock _financialClock;

    public DocumentVaultService(
        AppDbContext context,
        IDocumentVaultStore store,
        IOptionsMonitor<DocumentVaultOptions> options,
        ILogger<DocumentVaultService> logger,
        VaultDocumentTypeService? documentTypes = null,
        FinancialClock? financialClock = null)
    {
        _context = context;
        _store = store;
        _options = options;
        _logger = logger;
        _documentTypes = documentTypes;
        _financialClock = financialClock ?? FinancialClock.Utc;
    }

    public async Task<DocumentVaultCreateResult> CreateAsync(
        string originalFileName,
        byte[] fileData,
        int taxYear,
        string documentType,
        string? notes,
        string? transactionId,
        string? clientKey,
        CancellationToken ct = default)
    {
        var options = _options.CurrentValue;

        if (!_store.IsConfigured)
        {
            return new DocumentVaultCreateResult(DocumentVaultCreateStatus.StorageUnavailable, Message: "Document storage is not configured.");
        }

        if (string.IsNullOrWhiteSpace(originalFileName) || originalFileName.Length > 255 ||
            !IsAllowedTaxYear(taxYear) ||
            string.IsNullOrWhiteSpace(documentType) || documentType.Length > 40 ||
            notes?.Length > 500 ||
            transactionId?.Length > 450 ||
            clientKey?.Length > 64)
        {
            return new DocumentVaultCreateResult(DocumentVaultCreateStatus.InvalidMetadata, Message: "Document metadata is invalid.");
        }

        if (_documentTypes != null && !await _documentTypes.ExistsAsync(documentType, ct))
        {
            return new DocumentVaultCreateResult(
                DocumentVaultCreateStatus.InvalidMetadata,
                Message: "Choose an existing document type.");
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
            DocumentType = documentType,
            Notes = notes,
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

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(d => d.UploadedAt)
            .ThenByDescending(d => d.Id)
            .Skip(skip)
            .Take(take)
            .Select(d => new VaultDocumentDto(
                d.Id,
                d.OriginalFileName,
                d.ContentType,
                d.SizeBytes,
                d.TaxYear,
                d.DocumentType,
                d.Notes,
                d.TransactionId,
                d.UploadedAt,
                d.RetentionUntil))
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public Task<List<int>> GetAvailableTaxYearsAsync(CancellationToken ct = default) =>
        _context.VaultDocuments
            .AsNoTracking()
            .Select(document => document.TaxYear)
            .Distinct()
            .OrderByDescending(year => year)
            .ToListAsync(ct);

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
        string? documentType,
        string? notes,
        bool updateNotes,
        string? transactionId,
        bool updateTransactionId,
        CancellationToken ct = default)
    {
        var doc = await _context.VaultDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc == null) return null;

        if (taxYear.HasValue && !IsAllowedTaxYear(taxYear.Value))
        {
            return null;
        }
        if (documentType != null && _documentTypes != null &&
            !await _documentTypes.ExistsAsync(documentType, ct))
        {
            return null;
        }
        if (taxYear.HasValue && taxYear.Value != doc.TaxYear)
        {
            doc.TaxYear = taxYear.Value;
            doc.RetentionUntil = new DateOnly(taxYear.Value, 12, 31).AddYears(7);
        }
        if (documentType != null) doc.DocumentType = documentType;
        if (updateNotes)
        {
            doc.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes;
        }
        if (updateTransactionId)
        {
            doc.TransactionId = string.IsNullOrWhiteSpace(transactionId) ? null : transactionId;
        }

        await _context.SaveChangesAsync(ct);

        return new VaultDocumentDto(
            doc.Id,
            doc.OriginalFileName,
            doc.ContentType,
            doc.SizeBytes,
            doc.TaxYear,
            doc.DocumentType,
            doc.Notes,
            doc.TransactionId,
            doc.UploadedAt,
            doc.RetentionUntil);
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

    private bool IsAllowedTaxYear(int taxYear)
    {
        var currentYear = _financialClock.Today.Year;
        return taxYear >= currentYear - 7 && taxYear <= currentYear;
    }
}
