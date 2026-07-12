using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FinancialAppApi.Services;

public enum CreateTransactionCategoryStatus
{
    Created,
    Existing,
    NameRequired,
    DuplicateName,
    ReservedName
}

public enum DeleteTransactionCategoryStatus
{
    Deleted,
    NotFound,
    ReservedName,
    InUse,
    InvalidReplacement
}

public sealed record CreateTransactionCategoryResult(
    CreateTransactionCategoryStatus Status,
    TransactionCategory? Category = null,
    string? Message = null);

public sealed record DeleteTransactionCategoryResult(
    DeleteTransactionCategoryStatus Status,
    string? Message = null,
    int TransactionCount = 0,
    int RecurringPaymentCount = 0);

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

        // Idempotency: the offline outbox sends a client-generated id ("cat-...") and may replay
        // the same create on retry. If that exact row already exists, return it as success rather
        // than reporting a duplicate-name failure for the client's own not-yet-acknowledged write.
        if (!string.IsNullOrWhiteSpace(category.Id))
        {
            var existingById = await _context.TransactionCategories
                .FirstOrDefaultAsync(c => c.Id == category.Id);
            if (existingById != null)
            {
                return new CreateTransactionCategoryResult(CreateTransactionCategoryStatus.Existing, existingById);
            }
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

    public async Task<DeleteTransactionCategoryResult> DeleteCategoryAsync(string id, string? replacementCategoryId = null)
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

        var transactionMatches = await _context.Transactions
            .Where(t => t.Category.ToLower() == category.Name.ToLower())
            .ToListAsync();
        var recurringPaymentMatches = await _context.RecurringPayments
            .Where(rp => rp.Category.ToLower() == category.Name.ToLower())
            .ToListAsync();
        var isInUse = transactionMatches.Count > 0 || recurringPaymentMatches.Count > 0;

        if (isInUse)
        {
            if (string.IsNullOrWhiteSpace(replacementCategoryId))
            {
                return new DeleteTransactionCategoryResult(
                    DeleteTransactionCategoryStatus.InUse,
                    "Category is in use. Choose a replacement category before deleting it.",
                    transactionMatches.Count,
                    recurringPaymentMatches.Count);
            }

            var replacement = await _context.TransactionCategories.FindAsync(replacementCategoryId);
            if (replacement == null ||
                string.Equals(replacement.Id, category.Id, StringComparison.OrdinalIgnoreCase) ||
                IsReservedName(replacement.Name))
            {
                return new DeleteTransactionCategoryResult(
                    DeleteTransactionCategoryStatus.InvalidReplacement,
                    "Choose an existing non-reserved replacement category.",
                    transactionMatches.Count,
                    recurringPaymentMatches.Count);
            }

            foreach (var transaction in transactionMatches)
            {
                transaction.Category = replacement.Name;
            }

            foreach (var recurringPayment in recurringPaymentMatches)
            {
                recurringPayment.Category = replacement.Name;
            }
        }

        _context.TransactionCategories.Remove(category);
        await _context.SaveChangesAsync();
        _cache.Remove(CacheKey);

        return new DeleteTransactionCategoryResult(DeleteTransactionCategoryStatus.Deleted);
    }

    public async Task<bool> CategoryNameExistsAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return await _context.TransactionCategories
            .AnyAsync(c => c.Name.ToLower() == name.Trim().ToLower());
    }

    public void InvalidateCache()
    {
        _cache.Remove(CacheKey);
    }

    public static bool IsReservedName(string name)
    {
        return string.Equals(name, "Transfer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Adjustment", StringComparison.OrdinalIgnoreCase);
    }
}
