using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services.SavingsGoals;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public enum WishlistMutationStatus
{
    Success,
    IdMismatch,
    NotFound,
    NameRequired,
    PriceInvalid,
    AlreadyPurchased
}

public sealed record WishlistItemResult(
    WishlistMutationStatus Status,
    WishlistItem? Item = null,
    string? Message = null);

public sealed record WishlistPurchaseResult(
    WishlistMutationStatus Status,
    WishlistItem? Item = null,
    Transaction? Transaction = null,
    string? Message = null);

public sealed record WishlistItemProjection(
    int Id,
    string Name,
    decimal Price,
    string Priority,
    bool IsPurchased,
    DateTime? PurchasedAt,
    string? PurchaseTransactionId,
    DateTime CreatedAt,
    bool IsActive);

public sealed record PagedWishlist(
    IReadOnlyList<WishlistItemProjection> Items,
    int Total,
    int Page,
    int PageSize);

public class WishlistService
{
    private readonly AppDbContext _context;
    private readonly CycleBalanceService _cycleBalanceService;
    private readonly SavingsGoalService _savingsGoalService;
    private readonly FinancialClock _financialClock;

    public WishlistService(
        AppDbContext context,
        CycleBalanceService cycleBalanceService,
        SavingsGoalService savingsGoalService,
        FinancialClock? financialClock = null)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService;
        _savingsGoalService = savingsGoalService;
        _financialClock = financialClock ?? FinancialClock.Utc;
    }

    public async Task<List<WishlistItemProjection>> GetWishlistAsync(CancellationToken cancellationToken = default)
    {
        // Read-only: the controller maps these to DTOs and never mutates them, so skip change
        // tracking.
        return await _context.WishlistItems
            .AsNoTracking()
            .OrderByDescending(w => w.IsActive)
            .ThenByDescending(w => w.CreatedAt)
            .Select(w => new WishlistItemProjection(
                w.Id,
                w.Name,
                w.Price,
                w.Priority,
                w.IsPurchased,
                w.PurchasedAt,
                w.PurchaseTransactionId,
                w.CreatedAt,
                w.IsActive
            ))
            .ToListAsync(cancellationToken);
    }

    // Server-side pagination for the "Rewards Claimed" history. Only purchased items,
    // newest claim first. The active/queued wishlist still comes through GetWishlistAsync;
    // this keeps the history list bounded instead of shipping every past claim to the client.
    public async Task<PagedWishlist> GetClaimedWishlistPagedAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 5;
        if (pageSize > 100) pageSize = 100;

        var query = _context.WishlistItems
            .AsNoTracking()
            .Where(w => w.IsPurchased);

        var total = await query.CountAsync(cancellationToken);

        // Keep the offset calculation out of Int32 arithmetic. A caller can supply
        // int.MaxValue for page; multiplying that by pageSize would otherwise wrap
        // and can turn into an invalid negative Skip value.
        var offset = ((long)page - 1) * pageSize;
        if (offset >= total)
        {
            return new PagedWishlist([], total, page, pageSize);
        }

        var items = await query
            .OrderByDescending(w => w.PurchasedAt)
            .ThenByDescending(w => w.CreatedAt)
            // PurchasedAt and CreatedAt are not guaranteed to be unique. The PK
            // tie-breaker keeps rows from moving between pages across requests.
            .ThenByDescending(w => w.Id)
            .Skip((int)offset)
            .Take(pageSize)
            .Select(w => new WishlistItemProjection(
                w.Id,
                w.Name,
                w.Price,
                w.Priority,
                w.IsPurchased,
                w.PurchasedAt,
                w.PurchaseTransactionId,
                w.CreatedAt,
                w.IsActive
            ))
            .ToListAsync(cancellationToken);

        return new PagedWishlist(items, total, page, pageSize);
    }

    public async Task<WishlistItemResult> CreateWishlistItemAsync(WishlistItem item, CancellationToken cancellationToken = default)
    {
        // Idempotency: the offline outbox may replay a create on retry (e.g. the write committed
        // but the response was lost). The int PK is server-generated, so dedupe on the client-supplied
        // key instead — returning the already-created row rather than inserting a duplicate.
        if (!string.IsNullOrWhiteSpace(item.ClientKey))
        {
            var existing = await _context.WishlistItems.FirstOrDefaultAsync(w => w.ClientKey == item.ClientKey, cancellationToken);
            if (existing != null)
            {
                return new WishlistItemResult(WishlistMutationStatus.Success, existing);
            }
        }

        var validation = ValidateItem(item);
        if (validation != null)
        {
            return validation;
        }

        item.CreatedAt = DateTime.UtcNow;
        item.IsPurchased = false;
        item.PurchasedAt = null;
        item.PurchaseTransactionId = null;

        if (item.IsActive)
        {
            var activeItems = await _context.WishlistItems.Where(w => w.IsActive).ToListAsync(cancellationToken);
            foreach (var activeItem in activeItems)
            {
                activeItem.IsActive = false;
            }
        }
        else
        {
            var hasAny = await _context.WishlistItems.AnyAsync(cancellationToken);
            if (!hasAny)
            {
                item.IsActive = true;
            }
        }

        _context.WishlistItems.Add(item);
        await _context.SaveChangesAsync(cancellationToken);

        return new WishlistItemResult(WishlistMutationStatus.Success, item);
    }

    public async Task<WishlistItemResult> UpdateWishlistItemAsync(int id, WishlistItem updatedItem, CancellationToken cancellationToken = default)
    {
        if (id != updatedItem.Id)
        {
            return new WishlistItemResult(WishlistMutationStatus.IdMismatch, Message: "ID mismatch.");
        }

        var item = await _context.WishlistItems.FindAsync([id], cancellationToken);
        if (item == null)
        {
            return new WishlistItemResult(WishlistMutationStatus.NotFound);
        }

        var validation = ValidateItem(updatedItem);
        if (validation != null)
        {
            return validation;
        }

        item.Name = updatedItem.Name;
        item.Price = updatedItem.Price;
        item.Priority = updatedItem.Priority;

        if (updatedItem.IsActive && !item.IsActive)
        {
            var activeItems = await _context.WishlistItems.Where(w => w.IsActive && w.Id != id).ToListAsync(cancellationToken);
            foreach (var activeItem in activeItems)
            {
                activeItem.IsActive = false;
            }
            item.IsActive = true;
        }
        else if (!updatedItem.IsActive && item.IsActive)
        {
            item.IsActive = false;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return new WishlistItemResult(WishlistMutationStatus.Success, item);
    }

    public async Task<WishlistMutationStatus> DeleteWishlistItemAsync(int id, CancellationToken cancellationToken = default)
    {
        var item = await _context.WishlistItems.FindAsync([id], cancellationToken);
        if (item == null)
        {
            return WishlistMutationStatus.NotFound;
        }

        bool wasActive = item.IsActive;

        // A purchased item owns a ledger transaction (and possibly split rows). There is no
        // FK cascade from Transaction.WishlistItemId, so deleting the item alone would orphan
        // that transaction in the ledger. Remove it here to mirror UnpurchaseWishlistItemAsync,
        // otherwise undoing an add-then-purchase leaves the purchase record behind.
        var transaction = !string.IsNullOrWhiteSpace(item.PurchaseTransactionId)
            ? await _context.Transactions.FindAsync([item.PurchaseTransactionId], cancellationToken)
            : await _context.Transactions.FirstOrDefaultAsync(t => t.WishlistItemId == item.Id, cancellationToken);
        DateTime? affectedDate = transaction?.Date;
        if (transaction != null)
        {
            var splits = await _context.Transactions
                .Where(t => t.Id.StartsWith(transaction.Id + "-split-"))
                .ToListAsync(cancellationToken);
            _context.Transactions.RemoveRange(splits);
            _context.Transactions.Remove(transaction);
        }

        _context.WishlistItems.Remove(item);
        await _context.SaveChangesAsync(cancellationToken);

        if (affectedDate.HasValue)
        {
            var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
            if (setting != null)
            {
                var (cycleYear, cycleMonthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(TransactionDate.ToDateOnly(affectedDate.Value), setting.CycleDay);
                await _cycleBalanceService.InvalidateFromAsync(cycleYear, cycleMonthIndex);
            }
        }

        if (wasActive)
        {
            var nextItem = await _context.WishlistItems
                .Where(w => w.Id != item.Id && !w.IsPurchased)
                .OrderByDescending(w => w.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (nextItem != null)
            {
                var otherActiveItems = await _context.WishlistItems
                    .Where(w => w.IsActive && w.Id != nextItem.Id)
                    .ToListAsync(cancellationToken);
                foreach (var activeItem in otherActiveItems) activeItem.IsActive = false;
                nextItem.IsActive = true;
                await _context.SaveChangesAsync(cancellationToken);
            }
        }

        return WishlistMutationStatus.Success;
    }

    public async Task<WishlistPurchaseResult> PurchaseWishlistItemAsync(int id, DateTime? customDate = null, CancellationToken cancellationToken = default)
    {
        var item = await _context.WishlistItems.FindAsync([id], cancellationToken);
        if (item == null)
        {
            return new WishlistPurchaseResult(WishlistMutationStatus.NotFound);
        }
        if (item.IsPurchased)
        {
            var existingTransaction = !string.IsNullOrWhiteSpace(item.PurchaseTransactionId)
                ? await _context.Transactions.FindAsync([item.PurchaseTransactionId], cancellationToken)
                : await _context.Transactions.FirstOrDefaultAsync(
                    transaction => transaction.WishlistItemId == item.Id,
                    cancellationToken);
            return existingTransaction != null
                ? new WishlistPurchaseResult(WishlistMutationStatus.Success, item, existingTransaction)
                : new WishlistPurchaseResult(WishlistMutationStatus.AlreadyPurchased, Message: "Item is already purchased.");
        }

        var poolSummary = await _savingsGoalService.GetPoolSummaryAsync(cancellationToken);
        if (poolSummary.Unassigned < item.Price)
        {
            return new WishlistPurchaseResult(
                WishlistMutationStatus.PriceInvalid,
                Message: $"Insufficient free rewards to claim this item. Only {poolSummary.Unassigned:0.00} is unassigned.");
        }

        item.IsPurchased = true;
        var purchaseDate = customDate.HasValue ? TransactionDate.StartOfDate(TransactionDate.ToDateOnly(customDate.Value)) : TransactionDate.StartOfDate(_financialClock.Today);
        var purchasedAt = customDate ?? DateTime.UtcNow;
        item.PurchasedAt = purchaseDate;
        item.IsActive = false;

        var tx = new Transaction
        {
            Id = Guid.NewGuid().ToString("N"),
            Date = purchaseDate,
            PostedAt = purchasedAt,
            Description = $"Purchased: {item.Name} (Wish List)",
            Category = "Other",
            LedgerCategory = "Rewards",
            Amount = -item.Price,
            WishlistItemId = item.Id
        };

        item.PurchaseTransactionId = tx.Id;
        _context.Transactions.Add(tx);

        var strategy = _context.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                await using var dbTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);

                var nextItem = await _context.WishlistItems
                    .Where(w => w.Id != item.Id && !w.IsPurchased)
                    .OrderByDescending(w => w.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
                var activeItems = await _context.WishlistItems
                    .Where(w => w.IsActive && w.Id != item.Id)
                    .ToListAsync(cancellationToken);
                foreach (var activeItem in activeItems) activeItem.IsActive = false;
                if (nextItem != null) nextItem.IsActive = true;

                await _context.SaveChangesAsync(cancellationToken);

                var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
                if (setting != null)
                {
                    var (cycleYear, cycleMonthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(TransactionDate.ToDateOnly(tx.Date), setting.CycleDay);
                    await _cycleBalanceService.InvalidateFromAsync(cycleYear, cycleMonthIndex);
                }

                await dbTransaction.CommitAsync(cancellationToken);
            });
        }
        catch (DbUpdateException) when (_context.Database.IsRelational())
        {
            // IsPurchased is a concurrency token and WishlistItemId has a filtered unique
            // index. The loser of a simultaneous purchase race returns the winner's
            // persisted result, making network retries idempotent.
            _context.ChangeTracker.Clear();
            var purchasedItem = await _context.WishlistItems
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
            var existingTransaction = await _context.Transactions
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.WishlistItemId == id, cancellationToken);
            if (purchasedItem?.IsPurchased == true && existingTransaction != null)
            {
                return new WishlistPurchaseResult(WishlistMutationStatus.Success, purchasedItem, existingTransaction);
            }

            throw;
        }

        return new WishlistPurchaseResult(WishlistMutationStatus.Success, item, tx);
    }

    public async Task<WishlistPurchaseResult> UnpurchaseWishlistItemAsync(int id, CancellationToken cancellationToken = default)
    {
        var item = await _context.WishlistItems.FindAsync([id], cancellationToken);
        if (item == null)
        {
            return new WishlistPurchaseResult(WishlistMutationStatus.NotFound);
        }

        var purchaseTransactionId = item.PurchaseTransactionId;
        var transaction = !string.IsNullOrWhiteSpace(purchaseTransactionId)
            ? await _context.Transactions.FindAsync([purchaseTransactionId], cancellationToken)
            : null;

        DateTime? affectedDate = transaction?.Date;

        item.IsPurchased = false;
        item.PurchasedAt = null;
        item.PurchaseTransactionId = null;

        var hasActiveUnpurchased = await _context.WishlistItems
            .AnyAsync(w => w.Id != item.Id && !w.IsPurchased && w.IsActive, cancellationToken);
        if (!hasActiveUnpurchased)
        {
            item.IsActive = true;
        }

        if (transaction != null)
        {
            var splits = await _context.Transactions
                .Where(t => t.Id.StartsWith(transaction.Id + "-split-"))
                .ToListAsync(cancellationToken);
            _context.Transactions.RemoveRange(splits);
            _context.Transactions.Remove(transaction);
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            if (affectedDate.HasValue)
            {
                var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
                if (setting != null)
                {
                    var (cycleYear, cycleMonthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(TransactionDate.ToDateOnly(affectedDate.Value), setting.CycleDay);
                    await _cycleBalanceService.InvalidateFromAsync(cycleYear, cycleMonthIndex);
                }
            }

            await dbTransaction.CommitAsync(cancellationToken);
        });

        return new WishlistPurchaseResult(WishlistMutationStatus.Success, item, transaction);
    }

    private static WishlistItemResult? ValidateItem(WishlistItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Name))
        {
            return new WishlistItemResult(WishlistMutationStatus.NameRequired, Message: "Item name is required.");
        }
        if (item.Price <= 0)
        {
            return new WishlistItemResult(WishlistMutationStatus.PriceInvalid, Message: "Item price must be greater than zero.");
        }

        return null;
    }
}
