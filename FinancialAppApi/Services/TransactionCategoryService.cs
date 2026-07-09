using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FinancialAppApi.Services;

public enum CreateTransactionCategoryStatus
{
    Created,
    NameRequired,
    DuplicateName,
    ReservedName
}

public enum DeleteTransactionCategoryStatus
{
    Deleted,
    NotFound,
    ReservedName
}

public sealed record CreateTransactionCategoryResult(
    CreateTransactionCategoryStatus Status,
    TransactionCategory? Category = null,
    string? Message = null);

public sealed record DeleteTransactionCategoryResult(
    DeleteTransactionCategoryStatus Status,
    string? Message = null);

public class TransactionCategoryService
{
    // The category table has ~10 rows and changes rarely, but is read on nearly every
    // screen. Caching it in-process removes almost all repeat DB round-trips. A short
    // absolute expiration bounds cross-instance staleness.
    private const string CacheKey = "tx-categories";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;

    public TransactionCategoryService(AppDbContext context, IMemoryCache cache)
    {
        _context = context;
        _cache = cache;
    }

    public async Task<IReadOnlyList<TransactionCategory>> GetCategoriesAsync()
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

    public async Task<CreateTransactionCategoryResult> CreateCategoryAsync(TransactionCategory category)
    {
        if (string.IsNullOrWhiteSpace(category.Name))
        {
            return new CreateTransactionCategoryResult(
                CreateTransactionCategoryStatus.NameRequired,
                Message: "Category name is required.");
        }

        var exists = await _context.TransactionCategories
            .AnyAsync(c => c.Name.ToLower() == category.Name.ToLower());
        if (exists)
        {
            return new CreateTransactionCategoryResult(
                CreateTransactionCategoryStatus.DuplicateName,
                Message: $"Category '{category.Name}' already exists.");
        }

        if (IsReservedName(category.Name))
        {
            return new CreateTransactionCategoryResult(
                CreateTransactionCategoryStatus.ReservedName,
                Message: "Cannot create system-reserved category names.");
        }

        if (string.IsNullOrWhiteSpace(category.Id))
        {
            category.Id = $"cat-{Guid.NewGuid():N}";
        }

        _context.TransactionCategories.Add(category);
        await _context.SaveChangesAsync();
        _cache.Remove(CacheKey);

        return new CreateTransactionCategoryResult(CreateTransactionCategoryStatus.Created, category);
    }

    public async Task<DeleteTransactionCategoryResult> DeleteCategoryAsync(string id)
    {
        var category = await _context.TransactionCategories.FindAsync(id);
        if (category == null)
        {
            return new DeleteTransactionCategoryResult(DeleteTransactionCategoryStatus.NotFound);
        }

        if (IsReservedName(category.Name))
        {
            return new DeleteTransactionCategoryResult(
                DeleteTransactionCategoryStatus.ReservedName,
                "Cannot delete system-reserved categories.");
        }

        _context.TransactionCategories.Remove(category);
        await _context.SaveChangesAsync();
        _cache.Remove(CacheKey);

        return new DeleteTransactionCategoryResult(DeleteTransactionCategoryStatus.Deleted);
    }

    private static bool IsReservedName(string name)
    {
        return string.Equals(name, "Transfer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Adjustment", StringComparison.OrdinalIgnoreCase);
    }
}
