using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/categories")]
[AuthorizeToken]
public class TransactionCategoriesController : ControllerBase
{
    private readonly AppDbContext _context;

    public TransactionCategoriesController(AppDbContext context)
    {
        _context = context;
    }

    // GET: api/categories
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TransactionCategory>>> GetCategories()
    {
        return await _context.TransactionCategories
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    // POST: api/categories
    [HttpPost]
    public async Task<ActionResult<TransactionCategory>> PostCategory(TransactionCategory category)
    {
        if (string.IsNullOrWhiteSpace(category.Name))
        {
            return BadRequest(new { message = "Category name is required." });
        }

        // Check if category name already exists
        var exists = await _context.TransactionCategories
            .AnyAsync(c => c.Name.ToLower() == category.Name.ToLower());
        if (exists)
        {
            return BadRequest(new { message = $"Category '{category.Name}' already exists." });
        }

        if (string.Equals(category.Name, "Transfer", StringComparison.OrdinalIgnoreCase) || 
            string.Equals(category.Name, "Adjustment", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Cannot create system-reserved category names." });
        }

        if (string.IsNullOrWhiteSpace(category.Id))
        {
            category.Id = $"cat-{Guid.NewGuid().ToString("N")}";
        }

        _context.TransactionCategories.Add(category);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetCategories), new { id = category.Id }, category);
    }

    // DELETE: api/categories/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteCategory(string id)
    {
        var category = await _context.TransactionCategories.FindAsync(id);
        if (category == null)
        {
            return NotFound();
        }

        if (string.Equals(category.Name, "Transfer", StringComparison.OrdinalIgnoreCase) || 
            string.Equals(category.Name, "Adjustment", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Cannot delete system-reserved categories.");
        }

        _context.TransactionCategories.Remove(category);
        await _context.SaveChangesAsync();

        return NoContent();
    }
}
