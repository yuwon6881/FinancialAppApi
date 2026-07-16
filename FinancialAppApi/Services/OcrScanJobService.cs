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
    TooManyOutstandingJobs
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
    private const long MaxImageBytes = 10 * 1024 * 1024;

    private readonly AppDbContext _context;
    private readonly ReceiptScanRetentionPolicy _retentionPolicy;

    public OcrScanJobService(
        AppDbContext context,
        ReceiptScanRetentionPolicy retentionPolicy)
    {
        _context = context;
        _retentionPolicy = retentionPolicy;
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

        var outstandingJobs = await _context.ReceiptScanJobs.CountAsync(job =>
            job.Username == username &&
            (job.Status == "queued" || job.Status == "processing"));
        if (outstandingJobs >= _retentionPolicy.MaxOutstandingJobsPerUser)
        {
            return new CreateScanJobResult(
                CreateScanJobStatus.TooManyOutstandingJobs,
                Message: "Too many receipt scans are already in progress. Please wait for one to finish.");
        }

        byte[] imageData;
        await using (var ms = new MemoryStream())
        {
            await image.CopyToAsync(ms);
            imageData = ms.ToArray();
        }

        var mimeType = DetectSupportedMimeType(imageData);
        if (mimeType == null)
        {
            return new CreateScanJobResult(
                CreateScanJobStatus.UnsupportedImageType,
                Message: "Unsupported receipt image. Please use JPEG, PNG, WebP, HEIC, or HEIF.");
        }

        var now = DateTime.UtcNow;
        var job = new ReceiptScanJob
        {
            Id = $"ocr-{Guid.NewGuid():N}",
            Username = username,
            Status = "queued",
            MimeType = mimeType,
            ImageData = imageData,
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

        return new ScanJobResponse(
            job.Id,
            job.Status,
            result,
            job.ErrorMessage,
            job.CreatedAt,
            job.UpdatedAt,
            job.CompletedAt);
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
        job.ImageData = null;
        job.ErrorMessage = "Could not start receipt scan. Please try again.";
        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    private static string? DetectSupportedMimeType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 &&
            data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.StartsWith(pngSignature))
        {
            return "image/png";
        }

        if (data.Length >= 12 &&
            data[..4].SequenceEqual("RIFF"u8) &&
            data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (data.Length >= 12 && data.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = data.Slice(8, 4);
            if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) ||
                brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8))
            {
                return "image/heic";
            }
            if (brand.SequenceEqual("heif"u8) || brand.SequenceEqual("mif1"u8) ||
                brand.SequenceEqual("msf1"u8))
            {
                return "image/heif";
            }
        }

        return null;
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
