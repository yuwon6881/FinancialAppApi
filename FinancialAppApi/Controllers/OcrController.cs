using System.Security.Cryptography;
using System.Text;
using FinancialAppApi.Filters;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/ocr")]
public class OcrController : ControllerBase
{
    private readonly OcrScanJobService _scanJobService;
    private readonly ReceiptScanProcessor _processor;
    private readonly ReceiptScanTaskDispatcher _dispatcher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OcrController> _logger;

    public OcrController(
        OcrScanJobService scanJobService,
        ReceiptScanProcessor processor,
        ReceiptScanTaskDispatcher dispatcher,
        IConfiguration configuration,
        ILogger<OcrController> logger)
    {
        _scanJobService = scanJobService;
        _processor = processor;
        _dispatcher = dispatcher;
        _configuration = configuration;
        _logger = logger;
    }

    [AuthorizeToken]
    [EnableRateLimiting("ocr")]
    [HttpPost("scan-receipt/jobs")]
    // Leave room for multipart framing around the 10 MiB file enforced by the service.
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<IActionResult> CreateScanJobEndpoint(IFormFile? image)
    {
        var username = GetUsername();
        var userId = GetUserId();
        if (username == null || userId == null)
        {
            return Unauthorized(new { message = "Invalid session" });
        }

        var created = await _scanJobService.CreateScanJobAsync(
            userId,
            username,
            image,
            HttpContext.RequestAborted);
        if (created.Status == CreateScanJobStatus.TooManyOutstandingJobs)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { message = created.Message });
        }
        if (created.Status == CreateScanJobStatus.StorageUnavailable)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = created.Message });
        }
        if (created.Status != CreateScanJobStatus.Created)
        {
            return BadRequest(new { message = created.Message });
        }

        try
        {
            await _dispatcher.DispatchAsync(created.JobId!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch receipt scan job {JobId}.", created.JobId);
            await _scanJobService.MarkDispatchFailedAsync(created.JobId!);
            return StatusCode(503, new { message = "Could not start receipt scan. Please try again." });
        }

        return Accepted(new { scanId = created.JobId, status = "queued" });
    }

    [AuthorizeToken]
    [EnableRateLimiting("ocr")]
    [HttpPost("scan-receipt-split/jobs")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<IActionResult> CreateReceiptSplitScanJobEndpoint(IFormFile? image)
    {
        var username = GetUsername();
        var userId = GetUserId();
        if (username == null || userId == null)
            return Unauthorized(new { message = "Invalid session" });

        var created = await _scanJobService.CreateScanJobAsync(
            userId,
            username,
            image,
            HttpContext.RequestAborted,
            scanType: "receipt-split");
        if (created.Status == CreateScanJobStatus.TooManyOutstandingJobs)
            return StatusCode(StatusCodes.Status429TooManyRequests, new { message = created.Message });
        if (created.Status == CreateScanJobStatus.StorageUnavailable)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = created.Message });
        if (created.Status != CreateScanJobStatus.Created)
            return BadRequest(new { message = created.Message });

        try
        {
            await _dispatcher.DispatchAsync(created.JobId!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch receipt-split scan job {JobId}.", created.JobId);
            await _scanJobService.MarkDispatchFailedAsync(created.JobId!);
            return StatusCode(503, new { message = "Could not start receipt split scan. Please try again." });
        }

        return Accepted(new { scanId = created.JobId, status = "queued" });
    }

    [AuthorizeToken]
    [EnableRateLimiting("ocr")]
    [HttpPost("scan-investment/jobs")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<IActionResult> CreateInvestmentScanJobEndpoint(IFormFile? image)
    {
        var username = GetUsername();
        var userId = GetUserId();
        if (username == null || userId == null)
            return Unauthorized(new { message = "Invalid session" });

        var created = await _scanJobService.CreateScanJobAsync(
            userId,
            username,
            image,
            HttpContext.RequestAborted,
            scanType: "investment");
        if (created.Status == CreateScanJobStatus.TooManyOutstandingJobs)
            return StatusCode(StatusCodes.Status429TooManyRequests, new { message = created.Message });
        if (created.Status == CreateScanJobStatus.StorageUnavailable)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = created.Message });
        if (created.Status != CreateScanJobStatus.Created)
            return BadRequest(new { message = created.Message });

        try
        {
            await _dispatcher.DispatchAsync(created.JobId!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch investment scan job {JobId}.", created.JobId);
            await _scanJobService.MarkDispatchFailedAsync(created.JobId!);
            return StatusCode(503, new { message = "Could not start investment scan. Please try again." });
        }

        return Accepted(new { scanId = created.JobId, status = "queued" });
    }

    [AuthorizeToken]
    [HttpGet("scan-receipt/jobs/{jobId}")]
    public async Task<IActionResult> GetScanJob(string jobId)
    {
        var userId = GetUserId();
        if (userId == null)
        {
            return Unauthorized(new { message = "Invalid session" });
        }

        var job = await _scanJobService.GetScanJobAsync(userId, jobId, HttpContext.RequestAborted);
        if (job == null)
        {
            return NotFound(new { message = "Receipt scan job was not found." });
        }

        var response = new
        {
            scanId = job.ScanId,
            status = job.Status,
            result = job.Result,
            errorMessage = job.ErrorMessage,
            createdAt = job.CreatedAt,
            updatedAt = job.UpdatedAt,
            completedAt = job.CompletedAt
        };

        return Ok(response);
    }

    [AuthorizeToken]
    [HttpDelete("scan-receipt/jobs/{jobId}")]
    public async Task<IActionResult> DeleteScanJob(string jobId)
    {
        var userId = GetUserId();
        if (userId == null)
        {
            return Unauthorized(new { message = "Invalid session" });
        }

        if (!await _scanJobService.DeleteScanJobAsync(userId, jobId, HttpContext.RequestAborted))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Receipt image cleanup is temporarily unavailable. It will be retried automatically."
            });
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
            !SecureEquals(providedKey.ToString(), expectedKey))
        {
            return Unauthorized(new { message = "Invalid worker key." });
        }

        var status = await _processor.ProcessAsync(jobId);
        return status switch
        {
            // A missing job is permanent (explicitly deleted or expired by retention).
            // Acknowledge it so Cloud Tasks removes the task instead of retrying forever.
            ReceiptScanProcessStatus.NotFound => Ok(new { status = "gone" }),
            // A previous worker may have died after claiming the job. Returning 503 keeps
            // Cloud Tasks retrying until the short processing lease can be reclaimed.
            ReceiptScanProcessStatus.InProgress => StatusCode(503, new { message = "Receipt scan is still processing." }),
            _ => Ok(new { status = "processed" })
        };
    }

    private string? GetUsername()
    {
        return HttpContext.Items.TryGetValue("Username", out var value) ? value as string : null;
    }

    private string? GetUserId()
    {
        return HttpContext.Items.TryGetValue("UserId", out var value) ? value as string : null;
    }

    private static bool SecureEquals(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
