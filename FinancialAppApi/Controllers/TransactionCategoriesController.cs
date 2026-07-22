using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;
using FinancialAppApi.Services;
using FinancialAppApi.Database;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/categories")]
[AuthorizeToken]
public class TransactionCategoriesController : ControllerBase
{
    private readonly TransactionCategoryService _categoryService;
    private readonly CategorySuggestionService _categorySuggestionService;
    private readonly ILogger<TransactionCategoriesController> _logger;

    public TransactionCategoriesController(
        TransactionCategoryService categoryService,
        CategorySuggestionService categorySuggestionService,
        ILogger<TransactionCategoriesController> logger)
    {
        _categoryService = categoryService;
        _categorySuggestionService = categorySuggestionService;
        _logger = logger;
    }

    // GET: api/categories
    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> GetCategories()
    {
        var categories = await _categoryService.GetCategoriesAsync();
        return categories.Select(ToResponse).ToList();
    }

    // POST: api/categories
    [HttpPost]
    public async Task<ActionResult<TransactionCategory>> PostCategory(TransactionCategoryMutationDto dto)
    {
        var category = new TransactionCategory { Id = dto.Id, Name = dto.Name };
        var result = await _categoryService.CreateCategoryAsync(category);
        if (result.Status == CreateTransactionCategoryStatus.Existing)
        {
            // Idempotent replay of an already-committed create — return the stored row as success.
            return Ok(ToResponse(result.Category!));
        }
        if (result.Status != CreateTransactionCategoryStatus.Created)
        {
            return BadRequest(new { message = result.Message });
        }

        return CreatedAtAction(nameof(GetCategories), new { id = result.Category!.Id }, ToResponse(result.Category));
    }

    // PUT: api/categories/{id}/cycle-limit
    [HttpPut("{id}/cycle-limit")]
    public async Task<IActionResult> UpdateCycleLimit(string id, UpdateCategoryCycleLimitDto dto)
    {
        decimal? limit = null;
        if (!string.IsNullOrWhiteSpace(dto.CycleLimit))
        {
            try
            {
                limit = ObfuscationHelper.Deobfuscate(dto.CycleLimit);
            }
            catch (FormatException)
            {
                return BadRequest(new { message = "Cycle spending guide is invalid." });
            }
        }

        var result = await _categoryService.UpdateCycleLimitAsync(id, limit);
        if (result.Status == UpdateCategoryCycleLimitStatus.NotFound) return NotFound();
        if (result.Status == UpdateCategoryCycleLimitStatus.InvalidAmount)
            return BadRequest(new { message = result.Message });

        return Ok(ToResponse(result.Category!));
    }

    [HttpPost("suggest")]
    public async Task<IActionResult> SuggestCategories(CategorySuggestionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Description))
        {
            return BadRequest(new { message = "Description is required." });
        }
        if (request.Description.Length > 300 || request.Categories?.Count > 50 ||
            request.Categories?.Any(category => string.IsNullOrWhiteSpace(category) || category.Length > 60) == true)
        {
            return BadRequest(new { message = "Suggestion input is too long." });
        }

        try
        {
            var result = await _categorySuggestionService.SuggestAsync(
                request.Description.Trim(),
                request.TxType,
                request.Categories,
                cancellationToken);
            return result.Status == AiOperationStatus.Ok
                ? Ok(new { suggestions = result.Data })
                : StatusCode(503, new { message = result.Message });
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Category suggestion request timed out.");
            return StatusCode(503, new { message = "AI service timed out. Please try again." });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Category suggestion response was invalid JSON.");
            return StatusCode(503, new { message = "AI service returned an unreadable response. Please try again." });
        }
    }

    [HttpPost("suggest-notes")]
    public async Task<IActionResult> SuggestNotes(TransactionNoteSuggestionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Description))
        {
            return BadRequest(new { message = "Description is required." });
        }
        // Only guard against genuinely abusive payloads here. Individual history
        // entries (blank, overly long, or more than the model needs) are normalized
        // by SuggestNotesAsync itself, so rejecting on them would needlessly fail a
        // valid request -- e.g. a user with more than 10 past transactions.
        if (request.Description.Length > 300 || request.Category?.Length > 100 ||
            request.LedgerCategory?.Length > 50 || request.TxType?.Length > 20 ||
            request.HistoryDescriptions?.Count > 200)
        {
            return BadRequest(new { message = "Suggestion input is too long." });
        }

        try
        {
            var result = await _categorySuggestionService.SuggestNotesAsync(
                request.Description.Trim(),
                request.Category,
                request.LedgerCategory,
                request.TxType,
                request.HistoryDescriptions,
                cancellationToken);
            return result.Status == AiOperationStatus.Ok
                ? Ok(new { suggestions = result.Data })
                : StatusCode(503, new { message = result.Message });
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Transaction note suggestion request timed out.");
            return StatusCode(503, new { message = "AI service timed out. Please try again." });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Transaction note suggestion response was invalid JSON.");
            return StatusCode(503, new { message = "AI service returned an unreadable response. Please try again." });
        }
    }

    [HttpPost("cleanup/review")]
    public async Task<IActionResult> ReviewCleanup(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _categorySuggestionService.ReviewCategoryCleanupAsync(cancellationToken);
            return result.Status == AiOperationStatus.Ok
                ? Ok(result.Data)
                : StatusCode(503, new { message = result.Message });
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Category cleanup review request timed out.");
            return StatusCode(503, new { message = "AI service timed out. Please try again." });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Category cleanup review response was invalid JSON.");
            return StatusCode(503, new { message = "AI service returned an unreadable response. Please try again." });
        }
    }

    [HttpPost("cleanup/apply")]
    public async Task<IActionResult> ApplyCleanup(CategoryCleanupApplyRequest request)
    {
        try
        {
            var outcome = await _categorySuggestionService.ApplyCategoryCleanupAsync(request.Actions ?? []);
            return outcome.ConflictMessage != null
                ? Conflict(new { message = outcome.ConflictMessage })
                : Ok(outcome.Result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Category cleanup apply failed.");
            return StatusCode(503, new { message = "Could not apply category cleanup. Please try again." });
        }
    }

    // DELETE: api/categories/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteCategory(string id, [FromQuery] string? replacementCategoryId = null)
    {
        var result = await _categoryService.DeleteCategoryAsync(id, replacementCategoryId);
        if (result.Status == DeleteTransactionCategoryStatus.NotFound)
        {
            return NotFound();
        }
        if (result.Status == DeleteTransactionCategoryStatus.ReservedName)
        {
            return BadRequest(new { message = result.Message });
        }
        if (result.Status == DeleteTransactionCategoryStatus.InUse)
        {
            return Conflict(new
            {
                message = result.Message,
                transactionCount = result.TransactionCount,
                recurringPaymentCount = result.RecurringPaymentCount
            });
        }
        if (result.Status == DeleteTransactionCategoryStatus.InvalidReplacement)
        {
            return BadRequest(new
            {
                message = result.Message,
                transactionCount = result.TransactionCount,
                recurringPaymentCount = result.RecurringPaymentCount
            });
        }

        return NoContent();
    }

    public sealed record CategorySuggestionRequest(
        string Description,
        string? TxType,
        IReadOnlyList<string>? Categories);

    public sealed record TransactionNoteSuggestionRequest(
        string Description,
        string? Category,
        string? LedgerCategory,
        string? TxType,
        IReadOnlyList<string>? HistoryDescriptions);

    public sealed record CategoryCleanupApplyRequest(IReadOnlyList<CategoryCleanupAction>? Actions);

    private static object ToResponse(TransactionCategory category) => new
    {
        category.Id,
        category.Name,
        CycleLimit = category.CycleLimit.HasValue
            ? ObfuscationHelper.Obfuscate(category.CycleLimit.Value)
            : null
    };

}

public class TransactionCategoryMutationDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class UpdateCategoryCycleLimitDto
{
    public string? CycleLimit { get; set; }
}
