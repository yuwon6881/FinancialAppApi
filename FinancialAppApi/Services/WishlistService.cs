using FinancialAppApi.Database;
using FinancialAppApi.Models;
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

public class WishlistService
{
    private readonly AppDbContext _context;
    private readonly CycleBalanceService _cycleBalanceService;

    public WishlistService(AppDbContext context, CycleBalanceService cycleBalanceService)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService;
    }

    public async Task<List<WishlistItem>> GetWishlistAsync()
    {
        // Read-only: the controller maps these to DTOs and never mutates them, so skip change
        // tracking.
        return await _context.WishlistItems
            .AsNoTracking()
            .OrderByDescending(w => w.IsActive)
            .ThenByDescending(w => w.CreatedAt)
            .ToListAsync();
    }

    public async Task<WishlistItemResult> CreateWishlistItemAsync(WishlistItem item)
    {
        // Idempotency: the offline outbox may replay a create on retry (e.g. the write committed
        // but the response was lost). The int PK is server-generated, so dedupe on the client-supplied
        // key instead — returning the already-created row rather than inserting a duplicate.
        if (!string.IsNullOrWhiteSpace(item.ClientKey))
        {
            var existing = await _context.WishlistItems.FirstOrDefaultAsync(w => w.ClientKey == item.ClientKey);
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
            var activeItems = await _context.WishlistItems.Where(w => w.IsActive).ToListAsync();
            foreach (var activeItem in activeItems)
            {
                activeItem.IsActive = false;
            }
        }
        else
        {
            var hasAny = await _context.WishlistItems.AnyAsync();
            if (!hasAny)
            {
                item.IsActive = true;
            }
        }

        _context.WishlistItems.Add(item);
        await _context.SaveChangesAsync();

        return new WishlistItemResult(WishlistMutationStatus.Success, item);
    }

    public async Task<WishlistItemResult> UpdateWishlistItemAsync(int id, WishlistItem updatedItem)
    {
        if (id != updatedItem.Id)
        {
            return new WishlistItemResult(WishlistMutationStatus.IdMismatch, Message: "ID mismatch.");
        }

        var item = await _context.WishlistItems.FindAsync(id);
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
            var activeItems = await _context.WishlistItems.Where(w => w.IsActive && w.Id != id).ToListAsync();
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

        await _context.SaveChangesAsync();
        return new WishlistItemResult(WishlistMutationStatus.Success, item);
    }

    public async Task<WishlistMutationStatus> DeleteWishlistItemAsync(int id)
    {
        var item = await _context.WishlistItems.FindAsync(id);
        if (item == null)
        {
            return WishlistMutationStatus.NotFound;
        }

        bool wasActive = item.IsActive;

        _context.WishlistItems.Remove(item);
        await _context.SaveChangesAsync();

        if (wasActive)
        {
            var nextItem = await _context.WishlistItems
                .OrderByDescending(w => w.CreatedAt)
                .FirstOrDefaultAsync();
            if (nextItem != null)
            {
                nextItem.IsActive = true;
                await _context.SaveChangesAsync();
            }
        }

        return WishlistMutationStatus.Success;
    }

    public async Task<WishlistPurchaseResult> PurchaseWishlistItemAsync(int id)
    {
        var item = await _context.WishlistItems.FindAsync(id);
        if (item == null)
        {
            return new WishlistPurchaseResult(WishlistMutationStatus.NotFound);
        }
        if (item.IsPurchased)
        {
            return new WishlistPurchaseResult(WishlistMutationStatus.AlreadyPurchased, Message: "Item is already purchased.");
        }

        item.IsPurchased = true;
        item.PurchasedAt = DateTime.UtcNow;
        item.IsActive = false;

        var tx = new Transaction
        {
            Id = Guid.NewGuid().ToString("N"),
            Date = DateTime.UtcNow,
            Description = $"Purchased: {item.Name} (Wish List)",
            Category = "Other",
            LedgerCategory = "Rewards",
            Amount = -item.Price,
            WishlistItemId = item.Id
        };

        item.PurchaseTransactionId = tx.Id;
        _context.Transactions.Add(tx);

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync();
            await _context.SaveChangesAsync();

            var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
            if (setting != null)
            {
                var (cycleYear, cycleMonthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(TransactionDate.ToDateOnly(tx.Date), setting.CycleDay);
                await _cycleBalanceService.InvalidateFromAsync(cycleYear, cycleMonthIndex);
            }

            await dbTransaction.CommitAsync();
        });

        var nextItem = await _context.WishlistItems
            .Where(w => !w.IsPurchased)
            .OrderByDescending(w => w.CreatedAt)
            .FirstOrDefaultAsync();
        if (nextItem != null)
        {
            nextItem.IsActive = true;
            await _context.SaveChangesAsync();
        }

        return new WishlistPurchaseResult(WishlistMutationStatus.Success, item, tx);
    }

    public async Task<WishlistPurchaseResult> UnpurchaseWishlistItemAsync(int id)
    {
        var item = await _context.WishlistItems.FindAsync(id);
        if (item == null)
        {
            return new WishlistPurchaseResult(WishlistMutationStatus.NotFound);
        }

        var purchaseTransactionId = item.PurchaseTransactionId;
        var transaction = !string.IsNullOrWhiteSpace(purchaseTransactionId)
            ? await _context.Transactions.FindAsync(purchaseTransactionId)
            : null;

        DateTime? affectedDate = transaction?.Date;

        item.IsPurchased = false;
        item.PurchasedAt = null;
        item.PurchaseTransactionId = null;

        var hasActiveUnpurchased = await _context.WishlistItems
            .AnyAsync(w => w.Id != item.Id && !w.IsPurchased && w.IsActive);
        if (!hasActiveUnpurchased)
        {
            item.IsActive = true;
        }

        if (transaction != null)
        {
            var splits = await _context.Transactions
                .Where(t => t.Id.StartsWith(transaction.Id + "-split-"))
                .ToListAsync();
            _context.Transactions.RemoveRange(splits);
            _context.Transactions.Remove(transaction);
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync();
            await _context.SaveChangesAsync();

            if (affectedDate.HasValue)
            {
                var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
                if (setting != null)
                {
                    var (cycleYear, cycleMonthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(TransactionDate.ToDateOnly(affectedDate.Value), setting.CycleDay);
                    await _cycleBalanceService.InvalidateFromAsync(cycleYear, cycleMonthIndex);
                }
            }

            await dbTransaction.CommitAsync();
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
