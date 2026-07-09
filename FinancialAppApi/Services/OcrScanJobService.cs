using System.Text.Json.Nodes;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public enum CreateScanJobStatus
{
    Created,
    NoImage,
    ImageTooLarge
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
    private static readonly TimeSpan CompletedJobTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan StaleInProgressJobTtl = TimeSpan.FromHours(2);
    private const long MaxImageBytes = 10 * 1024 * 1024;

    private readonly AppDbContext _context;
    private readonly ILogger<OcrScanJobService> _logger;

    public OcrScanJobService(AppDbContext context, ILogger<OcrScanJobService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<CreateScanJobResult> CreateScanJobAsync(string username, IFormFile? image)
    {
        if (image == null || image.Length == 0)
        {
            return new CreateScanJobResult(CreateScanJobStatus.NoImage, Message: "No image file provided.");
        }

        if (image.Length > MaxImageBytes)
        {
            return new CreateScanJobResult(CreateScanJobStatus.ImageTooLarge, Message: "Receipt image is too large. Please use an image under 10 MB.");
        }

        await PruneOldScanJobsAsync();

        string base64Image;
        await using (var ms = new MemoryStream())
        {
            await image.CopyToAsync(ms);
            base64Image = Convert.ToBase64String(ms.ToArray());
        }

        var now = DateTime.UtcNow;
        var job = new ReceiptScanJob
        {
            Id = $"ocr-{Guid.NewGuid():N}",
            Username = username,
            Status = "queued",
            MimeType = NormalizeMimeType(image.ContentType),
            ImageBase64 = base64Image,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.ReceiptScanJobs.Add(job);
        await _context.SaveChangesAsync();

        return new CreateScanJobResult(CreateScanJobStatus.Created, job.Id);
    }

    public async Task<ScanJobResponse?> GetScanJobAsync(string username, string jobId)
    {
        var job = await _context.ReceiptScanJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId && j.Username == username);

        if (job == null)
        {
            return null;
        }

        object? result = null;
        if (!string.IsNullOrWhiteSpace(job.ResultJson))
        {
            result = ObfuscateReceiptScanAmount(job.ResultJson);
        }

        var response = new ScanJobResponse(
            job.Id,
            job.Status,
            result,
            job.ErrorMessage,
            job.CreatedAt,
            job.UpdatedAt,
            job.CompletedAt);

        if (job.Status == "completed" || job.Status == "failed")
        {
            await DeleteJobBestEffortAsync(jobId);
        }

        return response;
    }

    public async Task DeleteScanJobAsync(string username, string jobId)
    {
        var job = await _context.ReceiptScanJobs
            .FirstOrDefaultAsync(j => j.Id == jobId && j.Username == username);

        if (job != null)
        {
            _context.ReceiptScanJobs.Remove(job);
            await _context.SaveChangesAsync();
        }
    }

    public async Task MarkDispatchFailedAsync(string jobId)
    {
        var job = await _context.ReceiptScanJobs.FirstOrDefaultAsync(j => j.Id == jobId);
        if (job == null)
        {
            return;
        }

        job.Status = "failed";
        job.ImageBase64 = null;
        job.ErrorMessage = "Could not start receipt scan. Please try again.";
        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    private async Task PruneOldScanJobsAsync()
    {
        var now = DateTime.UtcNow;
        var completedCutoff = now - CompletedJobTtl;
        var staleCutoff = now - StaleInProgressJobTtl;

        var oldJobs = await _context.ReceiptScanJobs
            .Where(j =>
                ((j.Status == "completed" || j.Status == "failed") && j.UpdatedAt < completedCutoff) ||
                ((j.Status == "queued" || j.Status == "processing") && j.UpdatedAt < staleCutoff))
            .ToListAsync();

        if (oldJobs.Count == 0)
        {
            return;
        }

        _context.ReceiptScanJobs.RemoveRange(oldJobs);
        await _context.SaveChangesAsync();
    }

    private async Task DeleteJobBestEffortAsync(string jobId)
    {
        try
        {
            var job = await _context.ReceiptScanJobs.FindAsync(jobId);
            if (job != null)
            {
                _context.ReceiptScanJobs.Remove(job);
                await _context.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete receipt scan job {JobId} after delivering its result.", jobId);
        }
    }

    private static string NormalizeMimeType(string? mimeType)
    {
        return mimeType switch
        {
            "image/png" => "image/png",
            "image/webp" => "image/webp",
            "image/gif" => "image/gif",
            _ => "image/jpeg"
        };
    }

    private static object? ObfuscateReceiptScanAmount(string resultJson)
    {
        var node = JsonNode.Parse(resultJson);
        if (node is JsonObject obj &&
            obj.TryGetPropertyValue("amount", out var amountNode) &&
            amountNode is JsonValue amountValue &&
            amountValue.TryGetValue<decimal>(out var amount))
        {
            obj["amount"] = ObfuscationHelper.Obfuscate(amount);
        }

        return node;
    }
}
