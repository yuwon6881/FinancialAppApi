using System.Text.Json;
using FinancialAppApi.Filters;
using FinancialAppApi.Services.Documents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FinancialAppApi.Controllers;

/// <summary>
/// Manages long-lived tax document uploads and retrieval from the Document Vault.
/// </summary>
[ApiController]
[Route("api/documents")]
[AuthorizeToken]
[EnableRateLimiting("documents")]
public class DocumentsController : ControllerBase
{
    private readonly DocumentVaultService _service;

    public DocumentsController(DocumentVaultService service)
    {
        _service = service;
    }


    [HttpPost]
    [RequestSizeLimit(21 * 1024 * 1024)] // 20 MiB file plus multipart framing
    public async Task<IActionResult> Upload(
        [FromForm] IFormFile? file,
        [FromForm] int taxYear,
        [FromForm] string documentType,
        [FromForm] string? notes,
        [FromForm] string? transactionId,
        [FromForm] string? clientKey,
        CancellationToken ct)
    {
        // A missing or empty part must not reach CopyToAsync — model binding yields null
        // when the client omits `file`, which would surface as a 500 instead of a 400.
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { message = "No document was uploaded." });
        }

        byte[] data;
        using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms, ct);
            data = ms.ToArray();
        }

        var result = await _service.CreateAsync(
            file.FileName,
            data,
            taxYear,
            documentType,
            notes,
            transactionId,
            clientKey,
            ct);

        return result.Status switch
        {
            DocumentVaultCreateStatus.Created => Ok(new { id = result.DocumentId }),
            DocumentVaultCreateStatus.InvalidMetadata => BadRequest(new { message = result.Message }),
            DocumentVaultCreateStatus.FileTooLarge => BadRequest(new { message = result.Message }),
            DocumentVaultCreateStatus.QuotaExceeded => BadRequest(new { message = result.Message }),
            DocumentVaultCreateStatus.UnsupportedType => BadRequest(new { message = result.Message }),
            DocumentVaultCreateStatus.StorageUnavailable => StatusCode(503, new { message = result.Message }),
            _ => StatusCode(500)
        };
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? taxYear,
        [FromQuery] string? transactionId,
        [FromQuery] string? search,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 100);
        var (items, totalCount) = await _service.ListAsync(taxYear, transactionId, search, skip, take, ct);
        return Ok(new { items, totalCount });
    }

    [HttpGet("{id}/content")]
    public async Task<IActionResult> GetContent(int id, CancellationToken ct)
    {
        var result = await _service.GetContentAsync(id, ct);
        if (result == null) return NotFound();

        return File(result.Value.Data, result.Value.ContentType, result.Value.FileName);
    }

    [HttpGet("years")]
    public async Task<IActionResult> GetAvailableTaxYears(CancellationToken ct) =>
        Ok(await _service.GetAvailableTaxYearsAsync(ct));

    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(
        int id,
        [FromBody] UpdateDocumentRequest request,
        CancellationToken ct)
    {
        var notes = ReadOptionalString(request.Notes);
        var transactionId = ReadOptionalString(request.TransactionId);
        if (request.DocumentType is { Length: > 40 } ||
            request.DocumentType is not null && string.IsNullOrWhiteSpace(request.DocumentType) ||
            !IsOptionalString(request.Notes) ||
            !IsOptionalString(request.TransactionId) ||
            notes?.Length > 500 ||
            transactionId?.Length > 450)
        {
            return BadRequest(new { message = "Document metadata is invalid." });
        }

        var doc = await _service.UpdateAsync(
            id, 
            request.TaxYear, 
            request.DocumentType, 
            notes,
            request.Notes.ValueKind != JsonValueKind.Undefined,
            transactionId,
            request.TransactionId.ValueKind != JsonValueKind.Undefined,
            ct);
            
        if (doc == null) return NotFound();
        return Ok(doc);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var success = await _service.DeleteAsync(id, ct);
        if (!success) return StatusCode(500, new { message = "Failed to delete document from storage." });
        return NoContent();
    }

    [HttpGet("usage")]
    public async Task<IActionResult> GetUsage(CancellationToken ct)
    {
        var usage = await _service.GetUsageAsync(ct);
        return Ok(usage);
    }

    private static string? ReadOptionalString(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool IsOptionalString(JsonElement value) =>
        value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String;
}

/// <summary>
/// Request body for updating vault document metadata.
/// </summary>
public class UpdateDocumentRequest
{
    public int? TaxYear { get; set; }
    public string? DocumentType { get; set; }
    public JsonElement Notes { get; set; }
    public JsonElement TransactionId { get; set; }
}
