using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public enum TransactionMutationStatus
{
    Created,
    Existing,
    Updated,
    Deleted,
    NotFound,
    InvalidDate
}

public sealed record TransactionMutationRequest(
    string? Id,
    string? Date,
    string Description,
    string Category,
    string LedgerCategory,
    string Amount,
    string? RecurringPaymentId,
    int? WishlistItemId);

public sealed record TransactionMutationResult(
    TransactionMutationStatus Status,
    Transaction? Transaction = null,
    string? Message = null);

public class TransactionPersistenceService
{
    private readonly AppDbContext _context;
    private readonly CycleBalanceService _cycleBalanceService;

    public TransactionPersistenceService(AppDbContext context, CycleBalanceService cycleBalanceService)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService;
    }

    public async Task<TransactionMutationResult> CreateTransactionAsync(TransactionMutationRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Id))
        {
            var existingTx = await _context.Transactions.FirstOrDefaultAsync(t => t.Id == request.Id);
            if (existingTx != null)
            {
                return new TransactionMutationResult(TransactionMutationStatus.Existing, existingTx);
            }
        }

        if (!TransactionDate.TryParseInputDate(request.Date, out var postDate))
        {
            return InvalidDate();
        }

        var transaction = new Transaction
        {
            Id = string.IsNullOrWhiteSpace(request.Id) ? $"tx-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" : request.Id,
            Date = TransactionDate.FromInputDate(postDate),
            Description = request.Description,
            Category = request.Category,
            LedgerCategory = request.LedgerCategory,
            Amount = Math.Round(ObfuscationHelper.Deobfuscate(request.Amount), 2, MidpointRounding.AwayFromZero),
            RecurringPaymentId = request.RecurringPaymentId,
            WishlistItemId = request.WishlistItemId
        };

        var splitSpec = await ResolveIncomeSplitSpecAsync(transaction);

        _context.Transactions.Add(transaction);
        AddIncomeSplitTransactions(transaction, splitSpec);
        await ApplyWishlistPurchaseLinkAsync(transaction);

        await SaveAndInvalidateCycleBalancesAsync(transaction.Date);

        return new TransactionMutationResult(TransactionMutationStatus.Created, transaction);
    }

    public async Task<TransactionMutationResult> UpdateTransactionAsync(string id, TransactionMutationRequest request)
    {
        var transaction = await _context.Transactions.FindAsync(id);
        if (transaction == null)
        {
            return new TransactionMutationResult(TransactionMutationStatus.NotFound);
        }

        if (!TransactionDate.TryParseInputDate(request.Date, out var putDate))
        {
            return InvalidDate();
        }

        var originalDate = transaction.Date;

        var existingSplits = await _context.Transactions
            .Where(t => t.Id.StartsWith(transaction.Id + "-split-"))
            .ToListAsync();
        _context.Transactions.RemoveRange(existingSplits);

        transaction.Date = TransactionDate.PreserveTimeWhenSameDate(transaction.Date, putDate);
        transaction.Description = request.Description;
        transaction.Category = request.Category;
        transaction.LedgerCategory = request.LedgerCategory;
        transaction.Amount = Math.Round(ObfuscationHelper.Deobfuscate(request.Amount), 2, MidpointRounding.AwayFromZero);
        transaction.RecurringPaymentId = request.RecurringPaymentId ?? transaction.RecurringPaymentId;
        transaction.WishlistItemId = request.WishlistItemId ?? transaction.WishlistItemId;

        var splitSpec = await ResolveIncomeSplitSpecAsync(transaction);
        AddIncomeSplitTransactions(transaction, splitSpec);
        await ApplyWishlistPurchaseLinkAsync(transaction);

        await SaveAndInvalidateCycleBalancesAsync(originalDate < transaction.Date ? originalDate : transaction.Date);
        return new TransactionMutationResult(TransactionMutationStatus.Updated, transaction);
    }

    public async Task<TransactionMutationResult> DeleteTransactionAsync(string id)
    {
        var transaction = await _context.Transactions.FindAsync(id);
        if (transaction == null)
        {
            return new TransactionMutationResult(TransactionMutationStatus.NotFound);
        }

        await ClearWishlistPurchaseLinkAsync(transaction);

        var splits = await _context.Transactions
            .Where(t => t.Id.StartsWith(id + "-split-"))
            .ToListAsync();
        _context.Transactions.RemoveRange(splits);

        _context.Transactions.Remove(transaction);
        await SaveAndInvalidateCycleBalancesAsync(transaction.Date);

        return new TransactionMutationResult(TransactionMutationStatus.Deleted, transaction);
    }

    private async Task InvalidateCycleBalancesFromAsync(DateTime date)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
        if (setting == null) return;

        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(TransactionDate.ToDateOnly(date), setting.CycleDay);
        await _cycleBalanceService.InvalidateFromAsync(year, monthIndex);
    }

    private async Task SaveAndInvalidateCycleBalancesAsync(DateTime earliestAffectedDate)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync();
            await _context.SaveChangesAsync();
            await InvalidateCycleBalancesFromAsync(earliestAffectedDate);
            await dbTransaction.CommitAsync();
        });
    }

    private async Task<string> ResolveIncomeSplitSpecAsync(Transaction transaction)
    {
        if (string.Equals(transaction.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase))
        {
            var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
            if (setting != null)
            {
                return $"{setting.EssentialsAlloc * 100:0.##},{setting.GrowthAlloc * 100:0.##},{setting.StabilityAlloc * 100:0.##},{setting.RewardsAlloc * 100:0.##}";
            }
            return "";
        }

        if (!string.IsNullOrEmpty(transaction.LedgerCategory) && transaction.LedgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase))
        {
            var spec = transaction.LedgerCategory.Substring("IncomeSplit:".Length);
            transaction.LedgerCategory = "Income";
            return spec;
        }

        return "";
    }

    private void AddIncomeSplitTransactions(Transaction transaction, string splitSpec)
    {
        if (string.IsNullOrEmpty(splitSpec)) return;

        var parts = splitSpec.Split(',');
        if (parts.Length != 4) return;

        var categories = FinancialConstants.BudgetCategories;
        for (int i = 0; i < 4; i++)
        {
            if (decimal.TryParse(parts[i], out var pct) && pct > 0)
            {
                var splitAmount = Math.Round(transaction.Amount * (pct / 100m), 2, MidpointRounding.AwayFromZero);
                _context.Transactions.Add(new Transaction
                {
                    Id = $"{transaction.Id}-split-{categories[i]}",
                    Date = transaction.Date,
                    Description = $"[Split: {categories[i]}] {transaction.Description}",
                    Category = "Transfer",
                    LedgerCategory = $"Transfer:Income->{categories[i]}",
                    Amount = splitAmount
                });
            }
        }
    }

    private async Task ApplyWishlistPurchaseLinkAsync(Transaction transaction)
    {
        if (!transaction.WishlistItemId.HasValue) return;

        var item = await _context.WishlistItems.FindAsync(transaction.WishlistItemId.Value);
        if (item == null) return;

        item.IsPurchased = true;
        item.PurchasedAt ??= transaction.Date;
        item.PurchaseTransactionId = transaction.Id;
        item.IsActive = false;
    }

    private async Task ClearWishlistPurchaseLinkAsync(Transaction transaction)
    {
        WishlistItem? item = null;

        if (transaction.WishlistItemId.HasValue)
        {
            item = await _context.WishlistItems.FindAsync(transaction.WishlistItemId.Value);
        }

        item ??= await _context.WishlistItems
            .FirstOrDefaultAsync(w => w.PurchaseTransactionId == transaction.Id);

        if (item == null) return;

        item.IsPurchased = false;
        item.PurchasedAt = null;
        item.PurchaseTransactionId = null;

        var hasActiveUnpurchased = await _context.WishlistItems
            .AnyAsync(w => w.Id != item.Id && !w.IsPurchased && w.IsActive);
        if (!hasActiveUnpurchased)
        {
            item.IsActive = true;
        }
    }

    private static TransactionMutationResult InvalidDate()
    {
        return new TransactionMutationResult(
            TransactionMutationStatus.InvalidDate,
            Message: "Date must be in yyyy-MM-dd format.");
    }
}
