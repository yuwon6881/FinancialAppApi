using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public enum CreateScanJobStatus
{
    Created,
    NoImage,
    ImageTooLarge,
    UnsupportedImageType,
    TooManyOutstandingJobs,
    StorageUnavailable
}

public sealed record CreateScanJobResult(
    CreateScanJobStatus Status,
    string? JobId = null,
    string? Message = null);

public sealed record ScanJobResponse(
    string ScanId,
    string Status,
    object? Result,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt);

public class OcrScanJobService
{
    private readonly AppDbContext _context;
    private readonly ReceiptScanRetentionPolicy _retentionPolicy;
    private readonly IReceiptImageStore _imageStore;
    private readonly ILogger<OcrScanJobService> _logger;

    public OcrScanJobService(
        AppDbContext context,
        ReceiptScanRetentionPolicy retentionPolicy,
        IReceiptImageStore imageStore,
        ILogger<OcrScanJobService> logger)
    {
        _context = context;
        _retentionPolicy = retentionPolicy;
        _imageStore = imageStore;
        _logger = logger;
    }

    public async Task<CreateScanJobResult> CreateScanJobAsync(
        string userId,
        string username,
        IFormFile? image,
        CancellationToken cancellationToken = default,
        string scanType = "receipt")
    {
        if (image == null || image.Length == 0)
        {
            return new CreateScanJobResult(CreateScanJobStatus.NoImage, Message: "No image file provided.");
        }

        if (image.Length > GcsReceiptImageStore.MaxImageBytes)
        {
            return new CreateScanJobResult(CreateScanJobStatus.ImageTooLarge, Message: "Receipt image is too large. Please use an image under 10 MB.");
        }

        var outstandingJobs = await _context.ReceiptScanJobs.CountAsync(job =>
            job.UserId == userId &&
            (job.Status == "queued" || job.Status == "processing"),
            cancellationToken);
        if (outstandingJobs >= _retentionPolicy.MaxOutstandingJobsPerUser)
        {
            return new CreateScanJobResult(
                CreateScanJobStatus.TooManyOutstandingJobs,
                Message: "Too many receipt scans are already in progress. Please wait for one to finish.");
        }

        byte[] imageData;
        await using (var ms = new MemoryStream())
        {
            await image.CopyToAsync(ms, cancellationToken);
            imageData = ms.ToArray();
        }

        var mimeType = FileSignatureInspector.DetectMimeType(imageData);
        if (mimeType == null || !FileSignatureInspector.IsImage(mimeType))
        {
            return new CreateScanJobResult(
                CreateScanJobStatus.UnsupportedImageType,
                Message: "Unsupported receipt image. Please use JPEG, PNG, WebP, HEIC, or HEIF.");
        }

        var now = DateTime.UtcNow;
        var jobId = $"ocr-{Guid.NewGuid():N}";
        var storageObjectPath = $"{userId}/{jobId}{FileSignatureInspector.ExtensionForMimeType(mimeType)}";
        try
        {
            await _imageStore.UploadAsync(storageObjectPath, imageData, mimeType, cancellationToken);
        }
        catch (ReceiptImageStoreException exception)
        {
            _logger.LogError(exception, "Could not store receipt image for OCR job {JobId}.", jobId);
            return new CreateScanJobResult(
                CreateScanJobStatus.StorageUnavailable,
                Message: "Receipt image storage is temporarily unavailable. Please try again.");
        }

        var job = new ReceiptScanJob
        {
            Id = jobId,
            UserId = userId,
            Username = username,
            Status = "queued",
            MimeType = mimeType,
            ScanType = scanType,
            StorageObjectPath = storageObjectPath,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.ReceiptScanJobs.Add(job);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            try
            {
                await _imageStore.DeleteIfExistsAsync(storageObjectPath, cancellationToken);
            }
            catch (ReceiptImageStoreException cleanupException)
            {
                _logger.LogError(
                    cleanupException,
                    "Could not roll back receipt image {StorageObjectPath} after the OCR job failed to persist.",
                    storageObjectPath);
            }
            throw;
        }

        return new CreateScanJobResult(CreateScanJobStatus.Created, job.Id);
    }

    public async Task<ScanJobResponse?> GetScanJobAsync(
        string userId,
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await _context.ReceiptScanJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, cancellationToken);

        if (job == null)
        {
            return null;
        }

        object? result = null;
        if (!string.IsNullOrWhiteSpace(job.ResultJson))
        {
            result = ObfuscateScanNumbers(job.ResultJson, job.ScanType);
        }

        return new ScanJobResponse(
            job.Id,
            job.Status,
            result,
            job.ErrorMessage,
            job.CreatedAt,
            job.UpdatedAt,
            job.CompletedAt);
    }

    public async Task<bool> DeleteScanJobAsync(
        string userId,
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await _context.ReceiptScanJobs
            .FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, cancellationToken);

        if (job == null)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(job.StorageObjectPath))
        {
            try
            {
                await _imageStore.DeleteIfExistsAsync(job.StorageObjectPath, cancellationToken);
            }
            catch (ReceiptImageStoreException exception)
            {
                // Keep the row and path so retention cleanup can retry rather than
                // orphaning a sensitive receipt object with no database reference.
                _logger.LogError(exception, "Could not delete stored receipt image for OCR job {JobId}.", job.Id);
                return false;
            }
        }

        _context.ReceiptScanJobs.Remove(job);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task MarkDispatchFailedAsync(string jobId)
    {
        var job = await _context.ReceiptScanJobs.FirstOrDefaultAsync(j => j.Id == jobId);
        if (job == null)
        {
            return;
        }

        job.Status = "failed";
        job.ErrorMessage = "Could not start receipt scan. Please try again.";
        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await TryDeleteTerminalImageAsync(job);
    }



    private async Task TryDeleteTerminalImageAsync(ReceiptScanJob job)
    {
        if (string.IsNullOrWhiteSpace(job.StorageObjectPath)) return;

        try
        {
            await _imageStore.DeleteIfExistsAsync(job.StorageObjectPath);
        }
        catch (ReceiptImageStoreException exception)
        {
            // The terminal row deliberately retains the path so hourly retention
            // cleanup can retry the object deletion.
            _logger.LogError(exception, "Could not delete terminal receipt image for OCR job {JobId}.", job.Id);
            return;
        }

        job.StorageObjectPath = null;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (Exception exception)
        {
            // The terminal state is already durable. A stale path is safe because
            // retention cleanup treats an object-not-found response as success.
            _logger.LogError(exception, "Could not clear the deleted receipt object path for OCR job {JobId}.", job.Id);
        }
    }



    private static object? ObfuscateScanNumbers(string resultJson, string scanType)
    {
        var node = JsonNode.Parse(resultJson);
        if (node is not JsonObject obj) return node;

        if (scanType == "receipt-split")
        {
            ObfuscateReceiptSplitNumbers(obj);
            return node;
        }

        var fields = scanType == "investment"
            ? new[] { "units", "unitPrice", "cashAmount", "fees", "taxes", "toAmount" }
            : new[] { "amount" };
        foreach (var field in fields)
        {
            if (obj.TryGetPropertyValue(field, out var amountNode) &&
                amountNode is JsonValue amountValue &&
                amountValue.TryGetValue<decimal>(out var amount))
            {
                // The general amount obfuscator intentionally formats money to
                // cents, but scan results must retain every digit the model
                // extracted so the review form can show the source value.
                obj[field] = ObfuscateScanNumber(amount);
            }
        }

        return node;
    }

    private static void ObfuscateReceiptSplitNumbers(JsonObject obj)
    {
        ObfuscateObjectFields(obj, ["subtotal", "total"]);
        if (obj["items"] is JsonArray items)
        {
            foreach (var item in items.OfType<JsonObject>())
                ObfuscateObjectFields(item, ["unitPrice", "lineTotal"]);
        }
        if (obj["charges"] is JsonArray charges)
        {
            foreach (var charge in charges.OfType<JsonObject>())
                ObfuscateObjectFields(charge, ["amount"]);
        }
    }

    private static void ObfuscateObjectFields(JsonObject obj, IReadOnlyList<string> fields)
    {
        foreach (var field in fields)
        {
            if (obj.TryGetPropertyValue(field, out var valueNode) &&
                valueNode is JsonValue value &&
                value.TryGetValue<decimal>(out var amount))
            {
                obj[field] = ObfuscateScanNumber(amount);
            }
        }
    }

    private static string ObfuscateScanNumber(decimal value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.ToString("G29", CultureInfo.InvariantCulture));
        const string key = "FinancialAppObfuscationKey";
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = (byte)(bytes[index] ^ key[index % key.Length]);
        return Convert.ToBase64String(bytes);
    }
}
