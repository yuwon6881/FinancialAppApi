using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;
using FinancialAppApi.Services;

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
    public async Task<ActionResult<IEnumerable<TransactionCategory>>> GetCategories()
    {
        var categories = await _categoryService.GetCategoriesAsync();
        return categories.ToList();
    }

    // POST: api/categories
    [HttpPost]
    public async Task<ActionResult<TransactionCategory>> PostCategory(TransactionCategory category)
    {
        var result = await _categoryService.CreateCategoryAsync(category);
        if (result.Status != CreateTransactionCategoryStatus.Created)
        {
            return BadRequest(new { message = result.Message });
        }

        return CreatedAtAction(nameof(GetCategories), new { id = result.Category!.Id }, result.Category);
    }

    [HttpPost("suggest")]
    public async Task<IActionResult> SuggestCategories(CategorySuggestionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Description))
        {
            return BadRequest(new { message = "Description is required." });
        }

        try
        {
            var suggestions = await _categorySuggestionService.SuggestAsync(
                request.Description.Trim(),
                request.TxType,
                request.Categories);
            return Ok(new { suggestions });
        }
        catch (CategorySuggestionUserException ex)
        {
            return StatusCode(503, new { message = ex.Message });
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

    // DELETE: api/categories/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteCategory(string id)
    {
        var result = await _categoryService.DeleteCategoryAsync(id);
        if (result.Status == DeleteTransactionCategoryStatus.NotFound)
        {
            return NotFound();
        }
        if (result.Status == DeleteTransactionCategoryStatus.ReservedName)
        {
            return BadRequest(result.Message);
        }

        return NoContent();
    }

    public sealed record CategorySuggestionRequest(
        string Description,
        string? TxType,
        IReadOnlyList<string>? Categories);
}
