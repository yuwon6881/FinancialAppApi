using System.Text.Json;
using FinancialAppApi.Filters;
using FinancialAppApi.Services;
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
    private readonly DocumentContentService _contentService;
    private readonly DocumentRetentionService _retentionService;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(
        DocumentVaultService service,
        DocumentContentService contentService,
        DocumentRetentionService retentionService,
        ILogger<DocumentsController> logger)
    {
        _service = service;
        _contentService = contentService;
        _retentionService = retentionService;
        _logger = logger;
    }


    [HttpPost]
    [RequestSizeLimit(21 * 1024 * 1024)] // 20 MiB file plus multipart framing
    public async Task<IActionResult> Upload(
        [FromForm] IFormFile? file,
        [FromForm] int taxYear,
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
        // Checked before the copy below, not after: buffering up to 20 MiB into memory and *then*
        // refusing on a number in the form is work an unauthorised-shaped request should never buy.
        if (!_service.IsTaxYearAllowed(taxYear))
        {
            return BadRequest(new { message = _service.TaxYearValidationMessage });
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

        if (!_service.IsTaxYearAllowed(taxYear))
            return BadRequest(new { message = _service.TaxYearValidationMessage });

        var results = new List<object>();
        var storageUnavailable = false;
        var uploadedCount = 0;
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
                file.FileName, data, taxYear, null, null, reliefCategory, ct);
            if (result.Status == DocumentVaultCreateStatus.StorageUnavailable) storageUnavailable = true;
            if (result.Status == DocumentVaultCreateStatus.Created) uploadedCount++;
            results.Add(new
            {
                fileName = file.FileName,
                uploaded = result.Status == DocumentVaultCreateStatus.Created,
                id = result.DocumentId,
                message = result.Message
            });
        }

        // A batch where nothing landed because storage was down is not a successful request with
        // unlucky rows — reported as 200 it looked to the client exactly like files the user had
        // chosen badly, and it is the one failure that is worth retrying unchanged.
        if (storageUnavailable && uploadedCount == 0)
            return StatusCode(503, new { message = "Document storage is temporarily unavailable.", results });

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
        var document = await _contentService.GetMetadataAsync(id, ct);
        if (document == null) return NotFound();

        var etag = $"\"{document.Sha256}\"";
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "private, no-cache";
        if (MatchesIfNoneMatch(Request.Headers.IfNoneMatch, etag))
            return StatusCode(StatusCodes.Status304NotModified);

        var data = await _contentService.DownloadAsync(document, ct);
        if (data == null) return NotFound();

        // This response carries bytes the user uploaded, from the origin that holds their auth
        // cookie, so the browser must never be allowed to decide the type for itself.
        Response.Headers.XContentTypeOptions = "nosniff";

        // The browser PWA opens this endpoint directly in its PDF/image viewer. Mark those inline so
        // mobile Chrome does not treat the authenticated preview as a download or fail while opening
        // a blob-backed viewer. Anything else is an attachment: only images and PDFs are opened by a
        // viewer, and an inline-rendered document is the one shape that could run in this origin.
        // Nothing is lost — the preview sheet fetches XML/JSON itself and renders them as escaped
        // text, so it never depended on the browser opening them. The filename stays exposed either
        // way for the download helper, which reads Content-Disposition before saving the file.
        var isViewable = FileSignatureInspector.IsImage(document.ContentType) || document.ContentType == "application/pdf";
        var safeFileName = document.FileName.Replace("\"", string.Empty, StringComparison.Ordinal);
        var disposition = isViewable ? "inline" : "attachment";
        Response.Headers.ContentDisposition = $"{disposition}; filename=\"{safeFileName}\"; filename*=UTF-8''{Uri.EscapeDataString(document.FileName)}";
        return File(data, document.ContentType, enableRangeProcessing: true);
    }

    private static bool MatchesIfNoneMatch(IEnumerable<string> values, string etag)
    {
        static string StripWeak(string value) => value.StartsWith("W/", StringComparison.Ordinal)
            ? value[2..]
            : value;
        return values
            .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Any(candidate => candidate == "*" || StripWeak(candidate) == StripWeak(etag));
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
        return await StreamZipAsync(output => _service.WriteZipAsync(ids, output, ct));
    }

    /// <summary>
    /// Streams a ZIP, making a mid-stream failure visible. The 200 and the archive headers are
    /// already committed by the time the first object is fetched, so a stored object that has gone
    /// missing cannot be reported as a status code — left alone it produced a **truncated archive
    /// under HTTP 200**, which the client cannot tell from a complete one. Aborting the connection
    /// makes the transfer fail, which is what `downloadDocumentArchive` already reports as an error.
    /// </summary>
    private async Task<IActionResult> StreamZipAsync(Func<Stream, Task> write)
    {
        try
        {
            await write(Response.Body);
        }
        catch (DocumentVaultStoreException ex)
        {
            _logger.LogError(ex, "Aborting a vault ZIP export part-way through; the archive would have been incomplete.");
            HttpContext.Abort();
        }
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
            taxYear, request.Name, request.Limit, ct);
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
            taxYear, categoryId, request.Name, request.Limit, ct);
        return TaxReliefCategoryResult(result);
    }

    [HttpDelete("relief-categories/{taxYear:int}/{categoryId}")]
    public async Task<IActionResult> DeleteReliefCategory(
        int taxYear,
        string categoryId,
        CancellationToken ct)
    {
        var result = await _service.DeleteReliefCategoryAsync(taxYear, categoryId, ct);
        return result.Status == TaxReliefCategoryMutationStatus.Saved
            ? NoContent()
            : TaxReliefCategoryResult(result);
    }

    [HttpGet("summary/{taxYear:int}")]
    public async Task<IActionResult> GetTaxYearSummary(int taxYear, CancellationToken ct)
    {
        // An out-of-range year is a bad request, not a missing resource — the same answer
        // /relief-categories already gives for the same input.
        if (!_service.IsTaxYearAllowed(taxYear))
            return BadRequest(new { message = _service.TaxYearValidationMessage });

        var summary = await _service.GetTaxYearSummaryAsync(taxYear, ct);
        return summary == null ? NotFound() : Ok(summary);
    }

    /// <summary>
    /// Tax years at or near the end of the period they are worth keeping. Replaces the old
    /// <c>/expired</c> route, whose name stopped being true once the payload also carried years that
    /// have not expired yet.
    /// </summary>
    [HttpGet("retention")]
    public async Task<IActionResult> GetRetentionReview(CancellationToken ct) =>
        Ok(await _retentionService.GetReviewAsync(ct));

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] int? taxYear, CancellationToken ct)
    {
        if (!await _service.HasDocumentsForExportAsync(taxYear, ct))
            return NotFound(new { message = "There are no documents to export." });

        var suffix = taxYear?.ToString() ?? "all-tax-years";
        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = $"attachment; filename=\"tax-vault-{suffix}.zip\"";
        HttpContext.Features.Get<IHttpBodyControlFeature>()?.AllowSynchronousIO = true;
        return await StreamZipAsync(output => _service.WriteZipAsync(taxYear, output, ct));
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(
        int id,
        [FromBody] UpdateDocumentRequest request,
        CancellationToken ct)
    {
        var transactionId = ReadOptionalString(request.TransactionId);
        if (!IsOptionalString(request.TransactionId) ||
            transactionId?.Length > 450)
        {
            return BadRequest(new { message = "Document metadata is invalid." });
        }
        // The blank-category refusal is the service's rule, not this controller's — it applies to
        // every caller and it has to phrase itself the same way the bulk path does.
        var result = await _service.UpdateAsync(
            id,
            request.TaxYear,
            transactionId,
            request.TransactionId.ValueKind != JsonValueKind.Undefined,
            request.ReliefCategory,
            request.ReliefCategorySpecified,
            request.Amount,
            request.AmountSpecified,
            request.AmountCurrency,
            request.AmountStatus,
            ct);

        return DocumentUpdateResult(result);
    }

    /// <summary>
    /// Maps an update outcome to a status code. Only a genuinely absent row is a 404; every other
    /// refusal is a 400, because the document exists and it is the requested change that is not
    /// allowed. Both carry the reason, which is what the user is shown.
    /// </summary>
    private IActionResult DocumentUpdateResult(DocumentVaultUpdateResult result) => result.Status switch
    {
        DocumentVaultUpdateStatus.Updated => Ok(result.Document),
        DocumentVaultUpdateStatus.NotFound => NotFound(new { message = result.Message }),
        _ => BadRequest(new { message = result.Message })
    };

    [HttpPost("bulk-delete")]
    public async Task<IActionResult> BulkDelete([FromBody] BulkDeleteDocumentsRequest? request, CancellationToken ct)
    {
        var ids = request?.Ids?.Distinct().ToList() ?? [];
        if (ids.Count == 0) return BadRequest(new { message = "Choose at least one document." });
        if (ids.Count > 100) return BadRequest(new { message = "Delete at most 100 documents at a time." });

        var results = new List<object>();
        foreach (var id in ids)
        {
            var outcome = await _service.DeleteAsync(id, ct);
            // `deleted` stays true for an id that was already gone, so a replay still reads as
            // success and the client clears the row — but the message says which it was, so the
            // count in the toast is not quietly inflated by rows nobody removed.
            results.Add(new
            {
                id,
                deleted = outcome != DocumentDeleteOutcome.StorageFailed,
                message = outcome switch
                {
                    DocumentDeleteOutcome.AlreadyGone => "That document had already been removed.",
                    DocumentDeleteOutcome.StorageFailed => "The document could not be deleted from storage.",
                    _ => (string?)null
                }
            });
        }
        return Ok(new { results });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        // Deliberately 204 for an id that was already gone: deleting twice must not fail, and an
        // offline replay of a delete is a normal event rather than an error to report.
        var outcome = await _service.DeleteAsync(id, ct);
        if (outcome == DocumentDeleteOutcome.StorageFailed)
            return StatusCode(500, new { message = "Failed to delete document from storage." });
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
            TaxReliefCategoryMutationStatus.InUse => Conflict(new { message = "Move the documents filed under this category to another one before deleting it." }),
            _ => BadRequest(new { message = "Tax relief category details are invalid." })
        };
}

/// <summary>
/// Request body for updating vault document metadata.
/// </summary>
public class UpdateDocumentRequest
{
    public int? TaxYear { get; set; }
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
