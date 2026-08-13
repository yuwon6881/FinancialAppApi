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

public enum UpdateCategoryCycleLimitStatus
{
    Updated,
    NotFound,
    InvalidAmount
}

public sealed record UpdateCategoryCycleLimitResult(
    UpdateCategoryCycleLimitStatus Status,
    TransactionCategory? Category = null,
    string? Message = null);

public sealed record TransactionCategoryUsage(string CategoryKey, int Count);

public class TransactionCategoryService
{
    // The category table has ~10 rows and changes rarely, but is read on nearly every
    // screen. Caching it in-process removes almost all repeat DB round-trips. A short
    // absolute expiration bounds cross-instance staleness.
    private const string CacheKeyPrefix = "tx-categories:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly FinancialClock _financialClock;

    public TransactionCategoryService(AppDbContext context, IMemoryCache cache, FinancialClock? financialClock = null)
    {
        _context = context;
        _cache = cache;
        _financialClock = financialClock ?? FinancialClock.Utc;
    }

    public async Task<IReadOnlyList<TransactionCategory>> GetCategoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var categories = await _cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            return await _context.TransactionCategories
                .AsNoTracking()
                .OrderBy(c => c.Name)
                .ToListAsync(cancellationToken);
        });

        foreach (var category in categories!)
        {
            category.Type = CategoryFlowType.Normalize(category.Type);
        }

        return categories!;
    }

    public async Task<IReadOnlyList<TransactionCategoryUsage>> GetUsageAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        var start = TransactionDate.StartOfDate(startDate);
        var endExclusive = TransactionDate.ExclusiveEndOfDate(endDate);
        var usage = await _context.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.LedgerCategory != "Discarded"
                && transaction.Date >= start
                && transaction.Date < endExclusive)
            .GroupBy(transaction => transaction.Category.Trim().ToLower())
            .Select(group => new { CategoryKey = group.Key, Count = group.Count() })
            .OrderBy(row => row.CategoryKey)
            .ToListAsync(cancellationToken);
        return usage.Select(row => new TransactionCategoryUsage(row.CategoryKey, row.Count)).ToList();
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

        category.Type = CategoryFlowType.Normalize(category.Type);

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

        // Category spending guides are keyed by the category name rather than a foreign key so
        // historical reports survive category deletion. Close the guide as of the current cycle,
        // otherwise the dashboard would keep showing a tracked category that no longer exists.
        if (category.CycleLimit.HasValue ||
            await _context.CategorySpendingGuides.AnyAsync(guide => guide.CategoryName == category.Name))
        {
            await UpsertCurrentCycleGuideAsync(category.Name, null);
        }

        _context.TransactionCategories.Remove(category);
        await _context.SaveChangesAsync();
        _cache.Remove(CacheKey);

        return new DeleteTransactionCategoryResult(DeleteTransactionCategoryStatus.Deleted);
    }

    public async Task<UpdateCategoryCycleLimitResult> UpdateCycleLimitAsync(string id, decimal? cycleLimit)
    {
        return await UpdateCategoryAsync(id, type: null, cycleLimit, updateLimit: true);
    }

    public async Task<UpdateCategoryCycleLimitResult> UpdateCategoryAsync(string id, string? type, decimal? cycleLimit, bool updateLimit)
    {
        var category = await _context.TransactionCategories.FindAsync(id);
        if (category == null)
        {
            return new UpdateCategoryCycleLimitResult(UpdateCategoryCycleLimitStatus.NotFound);
        }

        var normalizedType = CategoryFlowType.Normalize(category.Type);
        if (type != null)
        {
            if (!CategoryFlowType.IsValid(type))
            {
                return new UpdateCategoryCycleLimitResult(
                    UpdateCategoryCycleLimitStatus.InvalidAmount,
                    Category: null,
                    Message: "Category flow type must be 'both', 'inflow', or 'outflow'.");
            }
            normalizedType = CategoryFlowType.Normalize(type);
        }

        if (updateLimit)
        {
            if (cycleLimit is <= 0 or > 9_999_999_999.99m)
            {
                return new UpdateCategoryCycleLimitResult(
                    UpdateCategoryCycleLimitStatus.InvalidAmount,
                    Category: null,
                    Message: "Cycle spending guide must be greater than zero and fit the supported currency range.");
            }

            if (cycleLimit.HasValue && !CategoryFlowType.AllowsSpendingGuide(normalizedType))
            {
                return new UpdateCategoryCycleLimitResult(
                    UpdateCategoryCycleLimitStatus.InvalidAmount,
                    Category: null,
                    Message: "Inflow categories cannot have cycle spending guides.");
            }
        }

        category.Type = normalizedType;
        if (!CategoryFlowType.AllowsSpendingGuide(normalizedType))
        {
            var hadCurrentOrHistoricalGuide = category.CycleLimit.HasValue ||
                await _context.CategorySpendingGuides.AnyAsync(guide => guide.CategoryName == category.Name);
            category.CycleLimit = null;
            if (hadCurrentOrHistoricalGuide)
            {
                await UpsertCurrentCycleGuideAsync(category.Name, null);
            }
        }
        else if (updateLimit)
        {
            category.CycleLimit = cycleLimit;
            await UpsertCurrentCycleGuideAsync(category.Name, cycleLimit);
        }

        await _context.SaveChangesAsync();
        _cache.Remove(CacheKey);

        return new UpdateCategoryCycleLimitResult(UpdateCategoryCycleLimitStatus.Updated, category);
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

    private async Task UpsertCurrentCycleGuideAsync(string categoryName, decimal? limitAmount)
    {
        var cycleKey = await GetCurrentCycleKeyAsync();
        var guide = await _context.CategorySpendingGuides.FirstOrDefaultAsync(item =>
            item.CategoryName == categoryName && item.EffectiveFromCycleKey == cycleKey);
        if (guide == null)
        {
            _context.CategorySpendingGuides.Add(new CategorySpendingGuide
            {
                Id = $"guide-{Guid.NewGuid():N}",
                CategoryName = categoryName,
                EffectiveFromCycleKey = cycleKey,
                LimitAmount = limitAmount
            });
        }
        else
        {
            guide.LimitAmount = limitAmount;
        }
    }

    private async Task<string> GetCurrentCycleKeyAsync()
    {
        var setting = await _context.FinancialSettings.AsNoTracking().FirstOrDefaultAsync();
        var cycleDay = setting?.CycleDay ?? FinancialConstants.DefaultCycleDay;
        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
            _financialClock.Today,
            cycleDay);
        return $"{year:D4}-{monthIndex:D2}";
    }

    private string CacheKey => CacheKeyPrefix + _context.RequireCurrentUserId();
}
