using System.Text.Json;
using FinancialAppApi.Filters;
using FinancialAppApi.Services.Documents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Http.Features;

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
        [FromForm] string? notes,
        [FromForm] string? transactionId,
        [FromForm] string? clientKey,
        [FromForm] string? reliefCategory,
        [FromForm] decimal? amount,
        [FromForm] string? amountCurrency,
        CancellationToken ct)
    {
        // A missing or empty part must not reach CopyToAsync — model binding yields null
        // when the client omits `file`, which would surface as a 500 instead of a 400.
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { message = "No document was uploaded." });
        }
        if (string.IsNullOrWhiteSpace(reliefCategory))
        {
            return BadRequest(new { message = "A tax relief category is required." });
        }
        if (file.Length > _service.GetConstraints().MaxDocumentBytes)
        {
            return BadRequest(new { message = "The document exceeds the maximum allowed size." });
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
            notes,
            transactionId,
            clientKey,
            reliefCategory,
            ct,
            amountOverride: amount,
            amountCurrencyOverride: amountCurrency);

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

    [HttpPost("bulk")]
    [RequestSizeLimit(205 * 1024 * 1024)]
    public async Task<IActionResult> BulkUpload(
        [FromForm] List<IFormFile>? files,
        [FromForm] int taxYear,
        [FromForm] string? notes,
        [FromForm] string? reliefCategory,
        CancellationToken ct)
    {
        var constraints = _service.GetConstraints();
        if (files == null || files.Count == 0)
            return BadRequest(new { message = "No documents were uploaded." });
        if (files.Count > constraints.MaxBulkDocuments)
            return BadRequest(new { message = $"Upload at most {constraints.MaxBulkDocuments} documents at a time." });
        if (string.IsNullOrWhiteSpace(reliefCategory))
            return BadRequest(new { message = "A tax relief category is required." });

        var results = new List<object>();
        foreach (var file in files)
        {
            if (file.Length == 0 || file.Length > constraints.MaxDocumentBytes)
            {
                results.Add(new
                {
                    fileName = file.FileName,
                    uploaded = false,
                    message = file.Length == 0 ? "The document is empty." : "The document exceeds the maximum allowed size."
                });
                continue;
            }

            byte[] data;
            await using (var stream = new MemoryStream())
            {
                await file.CopyToAsync(stream, ct);
                data = stream.ToArray();
            }

            var result = await _service.CreateAsync(
                file.FileName, data, taxYear, notes, null, null, reliefCategory, ct);
            results.Add(new
            {
                fileName = file.FileName,
                uploaded = result.Status == DocumentVaultCreateStatus.Created,
                id = result.DocumentId,
                message = result.Message
            });
        }

        return Ok(new { results });
    }

    [HttpPost("bulk-update-categories")]
    public async Task<IActionResult> BulkUpdateCategories(
        [FromBody] BulkUpdateDocumentCategoriesRequest? request,
        CancellationToken ct)
    {
        if (request?.Updates is not { Count: > 0 })
            return BadRequest(new { message = "Choose at least one document category to update." });
        if (request.Updates.Count > 100)
            return BadRequest(new { message = "Update at most 100 document categories at a time." });

        var updates = request.Updates
            .Select(update => new ReliefCategoryDocumentUpdate(update.Id, update.ReliefCategory))
            .ToArray();
        var results = await _service.UpdateReliefCategoriesAsync(updates, ct);
        return Ok(new { results });
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? taxYear,
        [FromQuery] string? transactionId,
        [FromQuery] string? reliefCategory,
        [FromQuery] string sort = "uploaded-desc",
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 100);
        var (items, totalCount) = await _service.ListAsync(taxYear, transactionId, reliefCategory, sort, skip, take, ct);
        return Ok(new { items, totalCount });
    }

    [HttpGet("{id}/content")]
    public async Task<IActionResult> GetContent(int id, CancellationToken ct)
    {
        var result = await _service.GetContentAsync(id, ct);
        if (result == null) return NotFound();

        return File(result.Value.Data, result.Value.ContentType, result.Value.FileName);
    }

    [HttpPost("export-selected")]
    public async Task<IActionResult> ExportSelected(
        [FromBody] BulkExportDocumentsRequest? request,
        CancellationToken ct)
    {
        var ids = request?.Ids?.Distinct().ToArray() ?? [];
        if (ids.Length == 0)
            return BadRequest(new { message = "Choose at least one document." });
        if (ids.Length > 100)
            return BadRequest(new { message = "Export at most 100 documents at a time." });
        if (!await _service.HasAllDocumentsForExportAsync(ids, ct))
            return NotFound(new { message = "One or more selected documents are no longer available." });

        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = "attachment; filename=\"tax-vault-selected.zip\"";
        HttpContext.Features.Get<IHttpBodyControlFeature>()?.AllowSynchronousIO = true;
        await _service.WriteZipAsync(ids, Response.Body, ct);
        return new EmptyResult();
    }

    [HttpGet("years")]
    public async Task<IActionResult> GetAvailableTaxYears(CancellationToken ct) =>
        Ok(await _service.GetAvailableTaxYearsAsync(ct));

    [HttpGet("constraints")]
    public IActionResult GetConstraints() => Ok(_service.GetConstraints());

    [HttpGet("relief-categories")]
    public async Task<IActionResult> GetReliefCategories([FromQuery] int taxYear, CancellationToken ct)
    {
        if (!_service.IsTaxYearAllowed(taxYear))
            return BadRequest(new { message = "Tax year is invalid." });

        return Ok(await _service.GetReliefCategoriesAsync(taxYear, ct));
    }

    [HttpPost("relief-categories/{taxYear:int}")]
    public async Task<IActionResult> AddReliefCategory(
        int taxYear,
        [FromBody] SaveTaxReliefCategoryRequest request,
        CancellationToken ct)
    {
        var result = await _service.AddReliefCategoryAsync(
            taxYear, request.Name, request.Limit, request.Detail, ct);
        return TaxReliefCategoryResult(result);
    }

    [HttpPatch("relief-categories/{taxYear:int}/{categoryId}")]
    public async Task<IActionResult> UpdateReliefCategory(
        int taxYear,
        string categoryId,
        [FromBody] SaveTaxReliefCategoryRequest request,
        CancellationToken ct)
    {
        var result = await _service.UpdateReliefCategoryAsync(
            taxYear, categoryId, request.Name, request.Limit, request.Detail, ct);
        return TaxReliefCategoryResult(result);
    }

    [HttpGet("summary/{taxYear:int}")]
    public async Task<IActionResult> GetTaxYearSummary(int taxYear, CancellationToken ct)
    {
        var summary = await _service.GetTaxYearSummaryAsync(taxYear, ct);
        return summary == null ? NotFound() : Ok(summary);
    }

    [HttpGet("expired")]
    public async Task<IActionResult> GetExpiredTaxYears(CancellationToken ct) =>
        Ok(await _service.GetExpiredTaxYearsAsync(ct));

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] int? taxYear, CancellationToken ct)
    {
        if (!await _service.HasDocumentsForExportAsync(taxYear, ct))
            return NotFound(new { message = "There are no documents to export." });

        var suffix = taxYear?.ToString() ?? "all-tax-years";
        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = $"attachment; filename=\"tax-vault-{suffix}.zip\"";
        HttpContext.Features.Get<IHttpBodyControlFeature>()?.AllowSynchronousIO = true;
        await _service.WriteZipAsync(taxYear, Response.Body, ct);
        return new EmptyResult();
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(
        int id,
        [FromBody] UpdateDocumentRequest request,
        CancellationToken ct)
    {
        var notes = ReadOptionalString(request.Notes);
        var transactionId = ReadOptionalString(request.TransactionId);
        if (!IsOptionalString(request.Notes) ||
            !IsOptionalString(request.TransactionId) ||
            notes?.Length > 500 ||
            transactionId?.Length > 450)
        {
            return BadRequest(new { message = "Document metadata is invalid." });
        }
        if (request.ReliefCategorySpecified && string.IsNullOrWhiteSpace(request.ReliefCategory))
        {
            return BadRequest(new { message = "A tax relief category is required." });
        }

        var doc = await _service.UpdateAsync(
            id, 
            request.TaxYear, 
            notes,
            request.Notes.ValueKind != JsonValueKind.Undefined,
            transactionId,
            request.TransactionId.ValueKind != JsonValueKind.Undefined,
            request.ReliefCategory,
            request.ReliefCategorySpecified,
            request.Amount,
            request.AmountSpecified,
            request.AmountCurrency,
            request.AmountStatus,
            ct);
            
        if (doc == null) return NotFound();
        return Ok(doc);
    }

    [HttpPost("bulk-delete")]
    public async Task<IActionResult> BulkDelete([FromBody] BulkDeleteDocumentsRequest? request, CancellationToken ct)
    {
        var ids = request?.Ids?.Distinct().ToList() ?? [];
        if (ids.Count == 0) return BadRequest(new { message = "Choose at least one document." });
        if (ids.Count > 100) return BadRequest(new { message = "Delete at most 100 documents at a time." });

        var results = new List<object>();
        foreach (var id in ids)
        {
            var deleted = await _service.DeleteAsync(id, ct);
            results.Add(new { id, deleted, message = deleted ? null : "The document could not be deleted from storage." });
        }
        return Ok(new { results });
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

    private IActionResult TaxReliefCategoryResult(TaxReliefCategoryMutationResult result) =>
        result.Status switch
        {
            TaxReliefCategoryMutationStatus.Saved => Ok(result.Category),
            TaxReliefCategoryMutationStatus.Duplicate => Conflict(new { message = "A category with this name already exists for the selected tax year." }),
            TaxReliefCategoryMutationStatus.NotFound => NotFound(),
            _ => BadRequest(new { message = "Tax relief category details are invalid." })
        };
}

/// <summary>
/// Request body for updating vault document metadata.
/// </summary>
public class UpdateDocumentRequest
{
    public int? TaxYear { get; set; }
    public JsonElement Notes { get; set; }
    public JsonElement TransactionId { get; set; }
    public string? ReliefCategory { get; set; }
    public bool ReliefCategorySpecified { get; set; }
    public decimal? Amount { get; set; }
    public bool AmountSpecified { get; set; }
    public string? AmountCurrency { get; set; }
    public string? AmountStatus { get; set; }
}

public sealed class SaveTaxReliefCategoryRequest
{
    public string? Name { get; set; }
    public decimal Limit { get; set; }
    public string? Detail { get; set; }
}

public sealed class BulkDeleteDocumentsRequest
{
    public List<int> Ids { get; set; } = [];
}

public sealed class BulkExportDocumentsRequest
{
    public List<int> Ids { get; set; } = [];
}

public sealed class BulkUpdateDocumentCategoriesRequest
{
    public List<BulkDocumentCategoryUpdate>? Updates { get; set; } = [];
}

public sealed class BulkDocumentCategoryUpdate
{
    public int Id { get; set; }
    public string? ReliefCategory { get; set; }
}
