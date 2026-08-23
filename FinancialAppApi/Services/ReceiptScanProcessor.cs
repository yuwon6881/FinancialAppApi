using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public record ReceiptScanResult(
    string Description,
    decimal? Amount,
    string? Date,
    string Category,
    string LedgerCategory,
    string TxType,
    double Confidence);

public sealed record ReceiptSplitItem(
    string Name,
    decimal Quantity,
    decimal? UnitPrice,
    decimal? LineTotal,
    double Confidence);

public sealed record ReceiptSplitCharge(
    string Label,
    string Kind,
    string Operation,
    string Basis,
    decimal? Amount,
    decimal? RatePercent,
    int Sequence,
    IReadOnlyList<int> EligibleItemIndexes,
    double Confidence);

public sealed record ReceiptSplitFieldConfidence(
    double Description,
    double Date,
    double Currency,
    double Subtotal,
    double Total);

public sealed record ReceiptSplitScanResult(
    string Description,
    string? Date,
    string? Currency,
    decimal? Subtotal,
    decimal? Total,
    string Category,
    string LedgerCategory,
    IReadOnlyList<ReceiptSplitItem> Items,
    IReadOnlyList<ReceiptSplitCharge> Charges,
    ReceiptSplitFieldConfidence FieldConfidence,
    bool Truncated,
    IReadOnlyList<string> Warnings,
    double Confidence);

public record InvestmentActivityScanResult(
    string? Type,
    Guid? AccountId,
    Guid? InstrumentId,
    string? TradeDate,
    decimal? Units,
    decimal? UnitPrice,
    decimal? CashAmount,
    decimal? Fees,
    decimal? Taxes,
    string? Currency,
    string? ToCurrency,
    decimal? ToAmount,
    double Confidence);

public enum ReceiptScanProcessStatus
{
    Processed,
    AlreadyFinished,
    InProgress,
    NotFound
}

public partial class ReceiptScanProcessor
{
    public static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(2);
    private readonly AiClient _aiClient;
    private readonly AppDbContext _context;
    private readonly IReceiptImageStore _imageStore;
    private readonly TransactionCategoryService _categoryService;
    private readonly ILogger<ReceiptScanProcessor> _logger;

    public ReceiptScanProcessor(
        AiClient aiClient,
        AppDbContext context,
        IReceiptImageStore imageStore,
        TransactionCategoryService categoryService,
        ILogger<ReceiptScanProcessor> logger)
    {
        _aiClient = aiClient;
        _context = context;
        _imageStore = imageStore;
        _categoryService = categoryService;
        _logger = logger;
    }

    public async Task<ReceiptScanProcessStatus> ProcessAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ReceiptScanJob? job;
        if (_context.Database.IsRelational())
        {
            var claimedAt = DateTime.UtcNow;
            var staleBefore = claimedAt - ProcessingLease;
            var claimed = await _context.ReceiptScanJobs
                .Where(j => j.Id == jobId &&
                    (j.Status == "queued" || (j.Status == "processing" && j.UpdatedAt < staleBefore)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(j => j.Status, "processing")
                    .SetProperty(j => j.UpdatedAt, claimedAt),
                    cancellationToken);
            if (claimed == 0)
            {
                var existingStatus = await _context.ReceiptScanJobs
                    .AsNoTracking()
                    .Where(j => j.Id == jobId)
                    .Select(j => j.Status)
                    .FirstOrDefaultAsync(cancellationToken);
                return existingStatus switch
                {
                    null => ReceiptScanProcessStatus.NotFound,
                    "completed" or "failed" => ReceiptScanProcessStatus.AlreadyFinished,
                    _ => ReceiptScanProcessStatus.InProgress
                };
            }
            job = await _context.ReceiptScanJobs
                .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        }
        else
        {
            job = await _context.ReceiptScanJobs
                .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
            if (job == null)
            {
                _logger.LogWarning("Receipt scan job {JobId} was not found.", jobId);
                return ReceiptScanProcessStatus.NotFound;
            }
            if (job.Status is "completed" or "failed") return ReceiptScanProcessStatus.AlreadyFinished;
            if (job.Status == "processing" && job.UpdatedAt >= DateTime.UtcNow - ProcessingLease)
                return ReceiptScanProcessStatus.InProgress;
            if (job.Status != "queued" && job.Status != "processing")
                return ReceiptScanProcessStatus.InProgress;
            job.Status = "processing";
            job.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
        }

        if (job == null)
        {
            _logger.LogWarning("Receipt scan job {JobId} was not found.", jobId);
            return ReceiptScanProcessStatus.NotFound;
        }

        // Worker requests are not authenticated HTTP requests. Once the job is claimed,
        // bind its owner so category lookups cannot see another user's categories.
        _context.SetCurrentUser(job.UserId);

        if (string.IsNullOrWhiteSpace(job.StorageObjectPath))
        {
            await MarkFailed(job, "Receipt image was not available for processing.", cancellationToken);
            return ReceiptScanProcessStatus.Processed;
        }

        byte[]? imageData;
        try
        {
            imageData = await _imageStore.DownloadAsync(job.StorageObjectPath, cancellationToken);
        }
        catch (ReceiptImageStoreException)
        {
            // Release the processing lease so Cloud Tasks (or the in-process
            // recovery loop) can retry a transient Storage API failure.
            job.Status = "queued";
            job.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            throw;
        }

        if (imageData is not { Length: > 0 })
        {
            await MarkFailed(job, "Receipt image was not available for processing.", cancellationToken);
            return ReceiptScanProcessStatus.Processed;
        }

        try
        {
            var encodedImage = Convert.ToBase64String(imageData);
            var outcome = job.ScanType switch
            {
                "investment" => await ScanInvestmentImageAsync(encodedImage, job.MimeType, cancellationToken),
                "receipt-split" => await ScanReceiptSplitImageAsync(encodedImage, job.MimeType, cancellationToken),
                _ => await ScanReceiptImageAsync(encodedImage, job.MimeType, cancellationToken)
            };
            if (outcome.ErrorMessage != null)
            {
                _logger.LogWarning("Receipt scan job {JobId} failed with user-facing error: {Message}", jobId, outcome.ErrorMessage);
                await MarkFailed(job, outcome.ErrorMessage, cancellationToken);
                return ReceiptScanProcessStatus.Processed;
            }

            job.Status = "completed";
            job.ResultJson = JsonSerializer.Serialize(outcome.Result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            job.ErrorMessage = null;
            job.CompletedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            await TryDeleteTerminalImageAsync(job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Receipt scan job {JobId} timed out.", jobId);
            await MarkFailed(job, "AI service timed out. Please try again.", cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Receipt scan job {JobId} returned invalid JSON.", jobId);
            await MarkFailed(job, "Could not read the receipt. Please try a clearer photo.", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while processing receipt scan job {JobId}.", jobId);
            await MarkFailed(job, "An unexpected error occurred. Please try again.", cancellationToken);
        }

        return ReceiptScanProcessStatus.Processed;
    }

    private async Task MarkFailed(
        ReceiptScanJob job,
        string message,
        CancellationToken cancellationToken)
    {
        job.Status = "failed";
        job.ErrorMessage = message;
        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await TryDeleteTerminalImageAsync(job, cancellationToken);
    }

    private async Task TryDeleteTerminalImageAsync(
        ReceiptScanJob job,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.StorageObjectPath)) return;

        try
        {
            await _imageStore.DeleteIfExistsAsync(job.StorageObjectPath, cancellationToken);
        }
        catch (ReceiptImageStoreException exception)
        {
            // Completion is already durable. Keep the object path on the terminal
            // row so retention cleanup can retry without losing the reference.
            _logger.LogError(exception, "Could not delete terminal receipt image for OCR job {JobId}.", job.Id);
            return;
        }

        job.StorageObjectPath = null;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The terminal status was saved before object deletion. If clearing the
            // now-stale path fails, cleanup treats a later Storage 404 as success.
            _logger.LogError(exception, "Could not clear the deleted receipt object path for OCR job {JobId}.", job.Id);
        }
    }

}
