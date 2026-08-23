using Microsoft.AspNetCore.Http.Features;
using FinancialAppApi.Services.Documents;
using Microsoft.AspNetCore.Mvc;

namespace FinancialAppApi.Controllers;

public partial class DocumentsController
{
    [HttpGet("years")]
    public async Task<IActionResult> GetAvailableTaxYears(CancellationToken ct) =>
        Ok(await _service.GetAvailableTaxYearsAsync(ct));

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview([FromQuery] int? taxYear, CancellationToken ct)
    {
        if (taxYear.HasValue && !_service.IsTaxYearAllowed(taxYear.Value))
            return BadRequest(new { message = _service.TaxYearValidationMessage });

        var availableYears = await _service.GetAvailableTaxYearsAsync(ct);
        var selectedTaxYear = taxYear ?? availableYears.FirstOrDefault();
        var usage = await _service.GetUsageAsync(ct);
        var retention = await _retentionService.GetReviewAsync(ct);
        var summary = selectedTaxYear == 0
            ? null
            : await _service.GetTaxYearSummaryAsync(selectedTaxYear, ct);
        var reliefCategories = selectedTaxYear == 0
            ? []
            : await _service.GetReliefCategoriesAsync(selectedTaxYear, ct);

        return Ok(new
        {
            usage,
            availableYears,
            retention,
            selectedTaxYear = selectedTaxYear == 0 ? (int?)null : selectedTaxYear,
            summary,
            reliefCategories,
        });
    }

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
}
