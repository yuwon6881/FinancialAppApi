using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Filters;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/ocr")]
public class OcrController : ControllerBase
{
    // GetScanJob deletes a job as soon as it hands back a terminal result, so
    // these TTLs only catch jobs whose result was never fetched (app closed
    // before polling again) or whose delete-on-fetch itself failed.
    private static readonly TimeSpan CompletedJobTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan StaleInProgressJobTtl = TimeSpan.FromHours(2);

    private readonly AppDbContext _context;
    private readonly ReceiptScanProcessor _processor;
    private readonly ReceiptScanTaskDispatcher _dispatcher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OcrController> _logger;

    public OcrController(
        AppDbContext context,
        ReceiptScanProcessor processor,
        ReceiptScanTaskDispatcher dispatcher,
        IConfiguration configuration,
        ILogger<OcrController> logger)
    {
        _context = context;
        _processor = processor;
        _dispatcher = dispatcher;
        _configuration = configuration;
        _logger = logger;
    }

    [AuthorizeToken]
    [HttpPost("scan-receipt/jobs")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> CreateScanJobEndpoint(IFormFile? image)
    {
        var created = await CreateScanJob(image);
        if (created.Result is not null)
        {
            return created.Result;
        }

        try
        {
            await _dispatcher.DispatchAsync(created.Value!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch receipt scan job {JobId}.", created.Value);
            var job = await _context.ReceiptScanJobs.FirstOrDefaultAsync(j => j.Id == created.Value);
            if (job != null)
            {
                job.Status = "failed";
                job.ImageBase64 = null;
                job.ErrorMessage = "Could not start receipt scan. Please try again.";
                job.CompletedAt = DateTime.UtcNow;
                job.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }
            return StatusCode(503, new { message = "Could not start receipt scan. Please try again." });
        }

        return Accepted(new { scanId = created.Value, status = "queued" });
    }

    [AuthorizeToken]
    [HttpGet("scan-receipt/jobs/{jobId}")]
    public async Task<IActionResult> GetScanJob(string jobId)
    {
        var username = GetUsername();
        if (username == null)
        {
            return Unauthorized(new { message = "Invalid session" });
        }

        var job = await _context.ReceiptScanJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId && j.Username == username);

        if (job == null)
        {
            return NotFound(new { message = "Receipt scan job was not found." });
        }

        object? result = null;
        if (!string.IsNullOrWhiteSpace(job.ResultJson))
        {
            result = JsonSerializer.Deserialize<JsonElement>(job.ResultJson);
        }

        var response = new
        {
            scanId = job.Id,
            status = job.Status,
            result,
            errorMessage = job.ErrorMessage,
            createdAt = job.CreatedAt,
            updatedAt = job.UpdatedAt,
            completedAt = job.CompletedAt
        };

        // The client now has the terminal result in hand, so the row no longer
        // needs to exist -- delete it here instead of waiting on the client to
        // acknowledge (that call can be skipped, e.g. the app is closed) or on
        // the TTL sweep below. Best-effort: a failed delete must not fail this
        // response, since the client already has what it asked for; PruneOldScanJobs
        // remains as the fallback if this delete itself can't reach the database.
        if (job.Status == "completed" || job.Status == "failed")
        {
            await DeleteJobBestEffort(jobId);
        }

        return Ok(response);
    }

    [AuthorizeToken]
    [HttpDelete("scan-receipt/jobs/{jobId}")]
    public async Task<IActionResult> DeleteScanJob(string jobId)
    {
        var username = GetUsername();
        if (username == null)
        {
            return Unauthorized(new { message = "Invalid session" });
        }

        var job = await _context.ReceiptScanJobs
            .FirstOrDefaultAsync(j => j.Id == jobId && j.Username == username);

        if (job != null)
        {
            _context.ReceiptScanJobs.Remove(job);
            await _context.SaveChangesAsync();
        }

        return NoContent();
    }

    [HttpPost("scan-receipt/jobs/{jobId}/process")]
    public async Task<IActionResult> ProcessScanJob(string jobId)
    {
        var expectedKey = _configuration["OcrWorkerKey"];
        if (string.IsNullOrWhiteSpace(expectedKey))
        {
            return StatusCode(503, new { message = "OCR worker key is not configured." });
        }

        if (!Request.Headers.TryGetValue("X-Ocr-Worker-Key", out var providedKey) ||
            providedKey.ToString() != expectedKey)
        {
            return Unauthorized(new { message = "Invalid worker key." });
        }

        await _processor.ProcessAsync(jobId);
        return Ok(new { status = "processed" });
    }

    private async Task<ActionResult<string>> CreateScanJob(IFormFile? image)
    {
        var username = GetUsername();
        if (username == null)
        {
            return Unauthorized(new { message = "Invalid session" });
        }

        if (image == null || image.Length == 0)
        {
            return BadRequest(new { message = "No image file provided." });
        }

        if (image.Length > 10 * 1024 * 1024)
        {
            return BadRequest(new { message = "Receipt image is too large. Please use an image under 10 MB." });
        }

        await PruneOldScanJobs();

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

        return job.Id;
    }

    private async Task PruneOldScanJobs()
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

    private async Task DeleteJobBestEffort(string jobId)
    {
        try
        {
            _context.ReceiptScanJobs.Remove(new ReceiptScanJob { Id = jobId });
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete receipt scan job {JobId} after delivering its result.", jobId);
        }
    }

    private string? GetUsername()
    {
        return HttpContext.Items.TryGetValue("Username", out var value) ? value as string : null;
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
}
