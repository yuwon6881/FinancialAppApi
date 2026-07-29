using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.IO.Compression;

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
public sealed record TaxReliefCategoryDefinition(string Id, string Name, decimal Limit, string Detail);
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
    int PolicyYear,
    bool IsPolicyProvisional,
    decimal ConfirmedAmount,
    decimal PendingReviewAmount,
    int DocumentCount,
    IReadOnlyList<TaxReliefCategorySummary> Categories);
public sealed record ExpiredTaxYearSummary(int TaxYear, int DocumentCount, long TotalBytes, DateOnly RetentionUntil);


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
    private readonly VaultAmountExtractor? _amountExtractor;

    public DocumentVaultService(
        AppDbContext context,
        IDocumentVaultStore store,
        IOptionsMonitor<DocumentVaultOptions> options,
        ILogger<DocumentVaultService> logger,
        VaultDocumentTypeService? documentTypes = null,
        FinancialClock? financialClock = null,
        VaultAmountExtractor? amountExtractor = null)
    {
        _context = context;
        _store = store;
        _options = options;
        _logger = logger;
        _documentTypes = documentTypes;
        _financialClock = financialClock ?? FinancialClock.Utc;
        _amountExtractor = amountExtractor;
    }

    public async Task<DocumentVaultCreateResult> CreateAsync(
        string originalFileName,
        byte[] fileData,
        int taxYear,
        string documentType,
        string? notes,
        string? transactionId,
        string? clientKey,
        string? reliefCategory = null,
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
            clientKey?.Length > 64 ||
            reliefCategory?.Length > 80 ||
            reliefCategory != null && !TaxReliefCatalog.IsValid(reliefCategory))
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

        var extraction = _amountExtractor == null
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
            DocumentType = documentType,
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
        if (documentType != null && _documentTypes != null &&
            !await _documentTypes.ExistsAsync(documentType, ct))
        {
            return null;
        }
        if (updateReliefCategory && reliefCategory != null && !TaxReliefCatalog.IsValid(reliefCategory))
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
        if (documentType != null) doc.DocumentType = documentType;
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
            doc.ReliefCategory = string.IsNullOrWhiteSpace(reliefCategory) ? null : reliefCategory;
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
            doc.DocumentType,
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
        if (documents.Count == 0) return null;

        var policyYear = TaxReliefCatalog.LatestPolicyYear;
        var definitions = TaxReliefCatalog.ForYear(policyYear);
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
        }).Where(summary => summary.DocumentCount > 0).ToList();

        return new TaxYearReliefSummary(
            taxYear,
            policyYear,
            taxYear != TaxReliefCatalog.LatestPolicyYear,
            summaries.Sum(summary => summary.ConfirmedAmount),
            summaries.Sum(summary => summary.PendingReviewAmount),
            documents.Count,
            summaries);
    }

    public IReadOnlyList<TaxReliefCategoryDefinition> GetReliefCategories(int taxYear) =>
        TaxReliefCatalog.ForYear(TaxReliefCatalog.LatestPolicyYear);

    public Task<bool> HasDocumentsForExportAsync(int? taxYear, CancellationToken ct = default)
    {
        var query = _context.VaultDocuments.AsNoTracking().AsQueryable();
        if (taxYear.HasValue) query = query.Where(document => document.TaxYear == taxYear.Value);
        return query.AnyAsync(ct);
    }

    public async Task WriteZipAsync(int? taxYear, Stream output, CancellationToken ct = default)
    {
        var query = _context.VaultDocuments.AsNoTracking().AsQueryable();
        if (taxYear.HasValue) query = query.Where(document => document.TaxYear == taxYear.Value);
        var documents = await query.OrderBy(document => document.TaxYear).ThenBy(document => document.Id).ToListAsync(ct);

        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var document in documents)
            {
                var data = await _store.DownloadAsync(document.StorageObjectPath, ct);
                if (data == null) continue;
                var safeName = Path.GetFileName(document.OriginalFileName);
                var entry = archive.CreateEntry($"{document.TaxYear}/{document.Id}-{safeName}", CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(data, ct);
            }
        }
    }

    private bool IsAllowedTaxYear(int taxYear)
    {
        var currentYear = _financialClock.Today.Year;
        return taxYear >= 2000 && taxYear <= currentYear;
    }
}

public static class TaxReliefCatalog
{
    public const int LatestPolicyYear = 2025;

    private static readonly IReadOnlyList<TaxReliefCategoryDefinition> Ya2025 =
    [
        new("parents-medical", "Parents and grandparents medical care", 8000, "Medical, dental, special needs and carer expenses."),
        new("supporting-equipment", "Basic supporting equipment", 6000, "For a disabled self, spouse, child or parent."),
        new("self-education", "Self education fees", 7000, "Qualifying study, upskilling and self-enhancement."),
        new("medical", "Medical expenses", 10000, "Qualifying serious disease, fertility, vaccination and dental expenses."),
        new("child-intervention", "Child assessment and intervention", 6000, "Qualifying assessment or early intervention for a child aged 18 or below."),
        new("lifestyle", "Lifestyle", 2500, "Books, devices, internet and qualifying courses."),
        new("sports", "Additional sports lifestyle", 1000, "Qualifying equipment, facilities, competitions and training."),
        new("breastfeeding", "Breastfeeding equipment", 1000, "Available once in every two years of assessment."),
        new("childcare", "Registered childcare or kindergarten", 3000, "For a child aged six or below."),
        new("sspn", "SSPN net savings", 8000, "Net deposits after withdrawals for the year."),
        new("life-epf", "Life insurance and EPF", 7000, "Sub-limits apply within this combined category."),
        new("prs", "Deferred annuity and PRS", 3000, "Qualifying deferred annuity and private retirement scheme payments."),
        new("education-medical-insurance", "Education and medical insurance", 4000, "Qualifying education and medical insurance premiums."),
        new("socso", "SOCSO contributions", 350, "Qualifying Social Security Organisation contributions."),
        new("ev-compost", "EV charging and composting equipment", 2500, "Qualifying non-business equipment."),
        new("first-home-interest-under-500k", "First-home interest (home up to RM500,000)", 7000, "Mutually exclusive first-home category; qualifying sale and purchase agreement conditions apply."),
        new("first-home-interest-over-500k", "First-home interest (home above RM500,000)", 5000, "Mutually exclusive first-home category for homes above RM500,000 up to RM750,000.")
    ];

    public static IReadOnlyList<TaxReliefCategoryDefinition> ForYear(int year) => Ya2025;
    public static bool IsValid(string id) => Ya2025.Any(category => category.Id == id);
}
