using System.Text.Json;
using FinancialAppApi.Services.Documents;
using Microsoft.AspNetCore.Mvc;

namespace FinancialAppApi.Controllers;

public partial class DocumentsController
{
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

        var outcomes = await _service.DeleteManyAsync(ids, ct);
        var results = new List<object>();
        foreach (var (id, outcome) in outcomes)
        {
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
