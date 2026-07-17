using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

/// <summary>
/// Applies individual category-cleanup actions (add/delete/merge and their undo
/// counterparts) inside the transaction that <see cref="CategorySuggestionService.ApplyCategoryCleanupAsync"/>
/// manages. Pure mechanism: mutates the change tracker and records undo actions;
/// the caller owns the transaction, save, and cache invalidation.
/// </summary>
public sealed class CategoryCleanupApplier
{
    internal sealed record CleanupStepResult(int AppliedCount, string? ConflictMessage)
    {
        public static CleanupStepResult Applied(int count) => new(count, null);
        public static CleanupStepResult Conflict(string message) => new(0, message);
    }

    private readonly AppDbContext _context;

    public CategoryCleanupApplier(AppDbContext context)
    {
        _context = context;
    }

    internal async Task<CleanupStepResult> ApplySingleCleanupActionAsync(CategoryCleanupAction action, List<CategoryCleanupAction> undoActions)
    {
        return action.Type.Trim().ToLowerInvariant() switch
        {
            "add" => CleanupStepResult.Applied(await ApplyAddCategoryAsync(action, undoActions)),
            "delete" => await ApplyDeleteCategoryAsync(action, undoActions),
            "merge" => await ApplyMergeCategoryAsync(action, undoActions),
            "deletebyname" => await ApplyDeleteByNameAsync(action),
            "restoretransactions" => CleanupStepResult.Applied(await ApplyRestoreTransactionsAsync(action)),
            "restorerecurringpayments" => CleanupStepResult.Applied(await ApplyRestoreRecurringPaymentsAsync(action)),
            _ => CleanupStepResult.Applied(0)
        };
    }

    private async Task<int> ApplyAddCategoryAsync(CategoryCleanupAction action, List<CategoryCleanupAction> undoActions)
    {
        var name = CategorySuggestionService.CleanCategoryName(action.NewCategoryName);
        if (name == null || TransactionCategoryService.IsReservedName(name))
        {
            return 0;
        }

        var exists = await _context.TransactionCategories.AnyAsync(c => c.Name.ToLower() == name.ToLower());
        if (exists)
        {
            return 0;
        }

        var categoryId = string.IsNullOrWhiteSpace(action.CategoryId) ? $"cat-{Guid.NewGuid():N}" : action.CategoryId.Trim();
        _context.TransactionCategories.Add(new TransactionCategory { Id = categoryId, Name = name });
        undoActions.Insert(0, new CategoryCleanupAction("deleteByName", Categories: [name]));
        return 1;
    }

    private async Task<CleanupStepResult> ApplyDeleteCategoryAsync(CategoryCleanupAction action, List<CategoryCleanupAction> undoActions)
    {
        var applied = 0;
        foreach (var name in NormalizeActionCategories(action.Categories))
        {
            var category = await _context.TransactionCategories.FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower());
            if (category == null || TransactionCategoryService.IsReservedName(category.Name))
            {
                continue;
            }

            var inUse = await _context.Transactions.AnyAsync(t => t.Category.ToLower() == category.Name.ToLower()) ||
                await _context.RecurringPayments.AnyAsync(rp => rp.Category.ToLower() == category.Name.ToLower());
            if (inUse)
            {
                return CleanupStepResult.Conflict("Category is in use. Choose a replacement category before deleting it.");
            }

            _context.TransactionCategories.Remove(category);
            undoActions.Insert(0, new CategoryCleanupAction("add", NewCategoryName: category.Name, CategoryId: category.Id));
            applied++;
        }

        return CleanupStepResult.Applied(applied);
    }

    private async Task<CleanupStepResult> ApplyMergeCategoryAsync(CategoryCleanupAction action, List<CategoryCleanupAction> undoActions)
    {
        var targetName = CategorySuggestionService.CleanCategoryName(action.TargetCategory);
        if (targetName == null || TransactionCategoryService.IsReservedName(targetName))
        {
            return CleanupStepResult.Applied(0);
        }

        var target = await _context.TransactionCategories.FirstOrDefaultAsync(c => c.Name.ToLower() == targetName.ToLower());
        if (target == null)
        {
            return CleanupStepResult.Conflict("Replacement category no longer exists.");
        }

        var applied = 0;
        foreach (var sourceName in NormalizeActionCategories(action.Categories))
        {
            if (string.Equals(sourceName, target.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = await _context.TransactionCategories.FirstOrDefaultAsync(c => c.Name.ToLower() == sourceName.ToLower());
            if (source == null || TransactionCategoryService.IsReservedName(source.Name))
            {
                continue;
            }

            var transactionIds = await _context.Transactions
                .Where(t => t.Category.ToLower() == source.Name.ToLower())
                .Select(t => t.Id)
                .ToListAsync();
            var recurringPaymentIds = await _context.RecurringPayments
                .Where(rp => rp.Category.ToLower() == source.Name.ToLower())
                .Select(rp => rp.Id)
                .ToListAsync();

            foreach (var transaction in await _context.Transactions.Where(t => transactionIds.Contains(t.Id)).ToListAsync())
            {
                transaction.Category = target.Name;
            }

            foreach (var recurringPayment in await _context.RecurringPayments.Where(rp => recurringPaymentIds.Contains(rp.Id)).ToListAsync())
            {
                recurringPayment.Category = target.Name;
            }

            _context.TransactionCategories.Remove(source);
            undoActions.Insert(0, new CategoryCleanupAction("restoreRecurringPayments", TargetCategory: source.Name, RecurringPaymentIds: recurringPaymentIds));
            undoActions.Insert(0, new CategoryCleanupAction("restoreTransactions", TargetCategory: source.Name, TransactionIds: transactionIds));
            undoActions.Insert(0, new CategoryCleanupAction("add", NewCategoryName: source.Name, CategoryId: source.Id));
            applied++;
        }

        return CleanupStepResult.Applied(applied);
    }

    private async Task<CleanupStepResult> ApplyDeleteByNameAsync(CategoryCleanupAction action)
    {
        var applied = 0;
        foreach (var name in NormalizeActionCategories(action.Categories))
        {
            var category = await _context.TransactionCategories.FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower());
            if (category == null || TransactionCategoryService.IsReservedName(category.Name))
            {
                continue;
            }

            var inUse = await _context.Transactions.AnyAsync(t => t.Category.ToLower() == category.Name.ToLower()) ||
                await _context.RecurringPayments.AnyAsync(rp => rp.Category.ToLower() == category.Name.ToLower());
            if (inUse)
            {
                return CleanupStepResult.Conflict("Undo could not remove a category that is now in use.");
            }

            _context.TransactionCategories.Remove(category);
            applied++;
        }

        return CleanupStepResult.Applied(applied);
    }

    private async Task<int> ApplyRestoreTransactionsAsync(CategoryCleanupAction action)
    {
        var target = CategorySuggestionService.CleanCategoryName(action.TargetCategory);
        var ids = NormalizeIds(action.TransactionIds);
        if (target == null || ids.Count == 0)
        {
            return 0;
        }

        var transactions = await _context.Transactions.Where(t => ids.Contains(t.Id)).ToListAsync();
        foreach (var transaction in transactions)
        {
            transaction.Category = target;
        }

        return transactions.Count;
    }

    private async Task<int> ApplyRestoreRecurringPaymentsAsync(CategoryCleanupAction action)
    {
        var target = CategorySuggestionService.CleanCategoryName(action.TargetCategory);
        var ids = NormalizeIds(action.RecurringPaymentIds);
        if (target == null || ids.Count == 0)
        {
            return 0;
        }

        var recurringPayments = await _context.RecurringPayments.Where(rp => ids.Contains(rp.Id)).ToListAsync();
        foreach (var recurringPayment in recurringPayments)
        {
            recurringPayment.Category = target;
        }

        return recurringPayments.Count;
    }

    private static IReadOnlyList<string> NormalizeActionCategories(IEnumerable<string>? categories)
    {
        return (categories ?? [])
            .Select(CategorySuggestionService.CleanCategoryName)
            .Where(name => name != null && !TransactionCategoryService.IsReservedName(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }

    private static IReadOnlyList<string> NormalizeIds(IEnumerable<string>? ids)
    {
        return (ids ?? [])
            .Select(id => id.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(1000)
            .ToList();
    }
}
