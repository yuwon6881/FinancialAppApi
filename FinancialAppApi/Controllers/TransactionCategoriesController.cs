using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/categories")]
[AuthorizeToken]
public class TransactionCategoriesController : ControllerBase
{
    // The category table has ~10 rows and changes rarely, but is read on nearly every
    // screen. Caching it in-process removes almost all of those repeat DB round-trips
    // (which matter on a low-connection free-tier Postgres). A short absolute expiration
    // bounds cross-instance staleness -- if another Cloud Run instance adds/deletes a
    // category, this instance's cache self-heals within CacheTtl.
    private const string CacheKey = "tx-categories";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;

    public TransactionCategoriesController(AppDbContext context, IMemoryCache cache)
    {
        _context = context;
        _cache = cache;
    }

    // GET: api/categories
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TransactionCategory>>> GetCategories()
    {
        var categories = await _cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            return await _context.TransactionCategories
                .AsNoTracking()
                .OrderBy(c => c.Name)
                .ToListAsync();
        });

        return categories!;
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
        _cache.Remove(CacheKey);

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
        _cache.Remove(CacheKey);

        return NoContent();
    }
}
