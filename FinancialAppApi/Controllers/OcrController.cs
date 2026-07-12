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
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> CreateScanJobEndpoint(IFormFile? image)
    {
        var username = GetUsername();
        if (username == null)
        {
            return Unauthorized(new { message = "Invalid session" });
        }

        var created = await _scanJobService.CreateScanJobAsync(username, image);
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
    [HttpGet("scan-receipt/jobs/{jobId}")]
    public async Task<IActionResult> GetScanJob(string jobId)
    {
        var username = GetUsername();
        if (username == null)
        {
            return Unauthorized(new { message = "Invalid session" });
        }

        var job = await _scanJobService.GetScanJobAsync(username, jobId);
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
        var username = GetUsername();
        if (username == null)
        {
            return Unauthorized(new { message = "Invalid session" });
        }

        await _scanJobService.DeleteScanJobAsync(username, jobId);

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

    private string? GetUsername()
    {
        return HttpContext.Items.TryGetValue("Username", out var value) ? value as string : null;
    }
}
