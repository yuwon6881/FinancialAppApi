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

    public TransactionCategoriesController(TransactionCategoryService categoryService)
    {
        _categoryService = categoryService;
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
}
