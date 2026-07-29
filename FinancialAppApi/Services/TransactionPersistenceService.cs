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
    InvalidDate,
    InvalidAmount,
    InvalidCategory,
    InvalidLedgerCategory,
    InvalidRecurringOccurrence,
    Conflict
}

public sealed record TransactionMutationRequest(
    string? Id,
    string? Date,
    string? PostedAt,
    string Description,
    string Category,
    string LedgerCategory,
    string Amount,
    string? RecurringPaymentId,
    int? WishlistItemId,
    string? RecurringOccurrenceDate = null);

public sealed record TransactionMutationResult(
    TransactionMutationStatus Status,
    Transaction? Transaction = null,
    string? Message = null);

public class TransactionPersistenceService
{
    private readonly AppDbContext _context;
    private readonly CycleBalanceService _cycleBalanceService;
    private readonly RecurringOccurrenceService _occurrenceService;

    public TransactionPersistenceService(
        AppDbContext context,
        CycleBalanceService cycleBalanceService,
        RecurringOccurrenceService occurrenceService)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService;
        _occurrenceService = occurrenceService;
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

        if (!await TransactionCategoryExistsAsync(request.Category))
        {
            return InvalidCategory(request.Category);
        }

        if (!TransactionDate.TryParseInputDate(request.Date, out var postDate))
        {
            return InvalidDate();
        }

        if (!ObfuscationHelper.TryDeobfuscate(request.Amount, out var decodedAmount)) return InvalidAmount();
        var amount = Math.Round(decodedAmount, 2, MidpointRounding.AwayFromZero);
        var ledgerValidation = ValidateAndNormalizeLedgerCategory(request.Category, request.LedgerCategory, amount);
        if (!ledgerValidation.IsValid)
        {
            return InvalidLedgerCategory(ledgerValidation.Message!);
        }

        var transaction = new Transaction
        {
            Id = string.IsNullOrWhiteSpace(request.Id) ? $"tx-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" : request.Id,
            Date = TransactionDate.FromInputDate(postDate),
            PostedAt = ResolvePostedAt(request.PostedAt),
            Description = request.Description,
            Category = ledgerValidation.Category,
            LedgerCategory = ledgerValidation.LedgerCategory,
            Amount = amount,
            RecurringPaymentId = request.RecurringPaymentId,
            WishlistItemId = request.WishlistItemId
        };
        var occurrence = await ResolveRecurringOccurrenceDateAsync(transaction, request.RecurringOccurrenceDate);
        if (!occurrence.IsValid)
        {
            return new TransactionMutationResult(TransactionMutationStatus.InvalidRecurringOccurrence, Message: occurrence.Message);
        }
        transaction.RecurringOccurrenceDate = occurrence.Date;

        var splitSpec = await ResolveIncomeSplitSpecAsync(transaction);

        _context.Transactions.Add(transaction);
        AddIncomeSplitTransactions(transaction, splitSpec);
        await ApplyWishlistPurchaseLinkAsync(transaction);

        try
        {
            await SaveAndInvalidateCycleBalancesAsync(transaction.Date);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation() && transaction.RecurringOccurrenceDate != null)
        {
            return new TransactionMutationResult(TransactionMutationStatus.Conflict, Message: "This recurring occurrence has already been settled.");
        }

        return new TransactionMutationResult(TransactionMutationStatus.Created, transaction);
    }

    public async Task<TransactionMutationResult> UpdateTransactionAsync(string id, TransactionMutationRequest request)
    {
        var transaction = await _context.Transactions.FindAsync(id);
        if (transaction == null)
        {
            return new TransactionMutationResult(TransactionMutationStatus.NotFound);
        }

        if (!await TransactionCategoryExistsAsync(request.Category))
        {
            return InvalidCategory(request.Category);
        }

        if (!TransactionDate.TryParseInputDate(request.Date, out var putDate))
        {
            return InvalidDate();
        }

        if (!ObfuscationHelper.TryDeobfuscate(request.Amount, out var decodedAmount)) return InvalidAmount();
        var amount = Math.Round(decodedAmount, 2, MidpointRounding.AwayFromZero);
        var ledgerValidation = ValidateAndNormalizeLedgerCategory(request.Category, request.LedgerCategory, amount);
        if (!ledgerValidation.IsValid)
        {
            return InvalidLedgerCategory(ledgerValidation.Message!);
        }

        var originalDate = transaction.Date;

        var existingSplits = await _context.Transactions
            .Where(t => t.Id.StartsWith(transaction.Id + "-split-"))
            .ToListAsync();
        _context.Transactions.RemoveRange(existingSplits);

        transaction.Date = TransactionDate.PreserveTimeWhenSameDate(transaction.Date, putDate);
        transaction.Description = request.Description;
        transaction.Category = ledgerValidation.Category;
        transaction.LedgerCategory = ledgerValidation.LedgerCategory;
        transaction.Amount = amount;
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

        var attachedDocuments = await _context.VaultDocuments
            .Where(document => document.TransactionId == id)
            .ToListAsync();
        foreach (var document in attachedDocuments)
        {
            document.TransactionId = null;
        }

        var splits = await _context.Transactions
            .Where(t => t.Id.StartsWith(id + "-split-"))
            .ToListAsync();
        _context.Transactions.RemoveRange(splits);

        _context.Transactions.Remove(transaction);
        await SaveAndInvalidateCycleBalancesAsync(transaction.Date);

        return new TransactionMutationResult(TransactionMutationStatus.Deleted, transaction);
    }

    // Tags a new transaction with the exact recurrence-engine billing date it settles, when it
    // was created against a recurring payment and its date lines up with that payment's cycle
    // occurrence. Legacy/manual transactions (no match, or no RecurringPaymentId) keep this null.
    private async Task<(bool IsValid, DateOnly? Date, string? Message)> ResolveRecurringOccurrenceDateAsync(
        Transaction transaction,
        string? requestedOccurrenceDate)
    {
        if (string.IsNullOrWhiteSpace(transaction.RecurringPaymentId))
        {
            return string.IsNullOrWhiteSpace(requestedOccurrenceDate)
                ? (true, null, null)
                : (false, null, "A recurring occurrence requires a recurring payment.");
        }

        var payment = await _context.RecurringPayments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == transaction.RecurringPaymentId);
        if (payment == null)
        {
            return string.IsNullOrWhiteSpace(requestedOccurrenceDate)
                ? (true, null, null)
                : (false, null, "The recurring payment could not be found.");
        }

        var setting = await _context.FinancialSettings.AsNoTracking().FirstOrDefaultAsync();
        var cycleDay = setting?.CycleDay ?? 1;

        var transactionDate = TransactionDate.ToDateOnly(transaction.Date);
        DateOnly candidateDate;
        if (!string.IsNullOrWhiteSpace(requestedOccurrenceDate))
        {
            if (!DateOnly.TryParseExact(requestedOccurrenceDate, "yyyy-MM-dd", out candidateDate))
            {
                return (false, null, "recurringOccurrenceDate must use yyyy-MM-dd format.");
            }
        }
        else
        {
            candidateDate = transactionDate;
        }

        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(candidateDate, cycleDay);
        var (cycleStart, cycleEnd, _) = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);

        DateOnly? matchedOccurrence = null;
        foreach (var billingDate in _occurrenceService.GetOccurrencesInRange(payment, cycleStart, cycleEnd, cycleDay))
        {
            if (DateOnly.FromDateTime(billingDate) == candidateDate)
            {
                matchedOccurrence = candidateDate;
                break;
            }
        }
        if (matchedOccurrence == null)
        {
            return string.IsNullOrWhiteSpace(requestedOccurrenceDate)
                ? (true, null, null)
                : (false, null, "The selected date is not an occurrence of this recurring payment.");
        }

        // Soft guard: never tag an occurrence that would collide with one already settled (e.g.
        // pay-early already recorded it). Leaving it null here keeps this normal ledger-entry
        // path from failing outright on that edge case, which the dedicated pay-early flow
        // already rejects explicitly.
        var alreadySettled = await _context.Transactions.AnyAsync(t =>
            t.RecurringPaymentId == payment.Id && t.RecurringOccurrenceDate == matchedOccurrence);

        return alreadySettled
            ? (false, null, "This recurring occurrence has already been settled.")
            : (true, matchedOccurrence, null);
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
        var percentages = new decimal[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!decimal.TryParse(
                    parts[i],
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out percentages[i]) || percentages[i] < 0)
            {
                return;
            }
        }

        var percentageTotal = percentages.Sum();
        if (percentageTotal <= 0) return;

        // Allocate in integer cents using the largest-remainder method. Rounding each bucket
        // independently can make the generated rows differ from the salary by one or more cents.
        var totalCents = decimal.ToInt64(transaction.Amount * 100m);
        var allocations = percentages
            .Select((percentage, index) =>
            {
                var exactCents = totalCents * percentage / percentageTotal;
                var floorCents = decimal.ToInt64(decimal.Floor(exactCents));
                return new { Index = index, Cents = floorCents, Fraction = exactCents - floorCents };
            })
            .ToArray();

        var allocatedCents = allocations.Sum(allocation => allocation.Cents);
        var remainingCents = totalCents - allocatedCents;
        var remainderOrder = allocations
            .OrderByDescending(allocation => allocation.Fraction)
            .ThenBy(allocation => allocation.Index)
            .Select(allocation => allocation.Index)
            .ToArray();
        var finalCents = allocations.Select(allocation => allocation.Cents).ToArray();
        for (long i = 0; i < remainingCents; i++)
        {
            finalCents[remainderOrder[(int)(i % remainderOrder.Length)]]++;
        }

        for (var i = 0; i < finalCents.Length; i++)
        {
            if (finalCents[i] <= 0) continue;
            _context.Transactions.Add(new Transaction
            {
                Id = $"{transaction.Id}-split-{categories[i]}",
                Date = transaction.Date,
                PostedAt = transaction.PostedAt,
                Description = $"[Split: {categories[i]}] {transaction.Description}",
                Category = "Transfer",
                LedgerCategory = $"Transfer:Income->{categories[i]}",
                Amount = finalCents[i] / 100m
            });
        }
    }

    private async Task ApplyWishlistPurchaseLinkAsync(Transaction transaction)
    {
        if (!transaction.WishlistItemId.HasValue) return;

        var item = await _context.WishlistItems.FindAsync(transaction.WishlistItemId.Value);
        if (item == null) return;

        item.IsPurchased = true;
        item.PurchasedAt = transaction.Date;
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

    private static DateTime ResolvePostedAt(string? value)
    {
        if (DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            var utc = parsed.UtcDateTime;
            if (utc >= DateTime.UnixEpoch && utc <= DateTime.UtcNow.AddDays(1))
            {
                return utc;
            }
        }

        return DateTime.UtcNow;
    }

    private static TransactionMutationResult InvalidAmount() => new(
        TransactionMutationStatus.InvalidAmount,
        Message: "Amount is malformed.");

    private async Task<bool> TransactionCategoryExistsAsync(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return false;
        }

        if (TransactionCategoryService.IsReservedName(category))
        {
            return true;
        }

        return await _context.TransactionCategories.AnyAsync(c => c.Name.ToLower() == category.Trim().ToLower());
    }

    private static TransactionMutationResult InvalidCategory(string category)
    {
        var name = string.IsNullOrWhiteSpace(category) ? "Category" : $"Category '{category}'";
        return new TransactionMutationResult(
            TransactionMutationStatus.InvalidCategory,
            Message: $"{name} does not exist.");
    }

    private static TransactionMutationResult InvalidLedgerCategory(string message)
    {
        return new TransactionMutationResult(
            TransactionMutationStatus.InvalidLedgerCategory,
            Message: message);
    }

    private static (bool IsValid, string Category, string LedgerCategory, string? Message)
        ValidateAndNormalizeLedgerCategory(string category, string ledgerCategory, decimal amount)
    {
        var normalizedCategory = category.Trim();
        var normalizedLedger = ledgerCategory?.Trim() ?? string.Empty;
        var isTransferCategory = normalizedCategory.Equals("Transfer", StringComparison.OrdinalIgnoreCase);

        if (normalizedLedger.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase))
        {
            if (!isTransferCategory)
            {
                return (false, normalizedCategory, normalizedLedger,
                    "A transfer ledger route must use the Transfer category.");
            }

            var route = normalizedLedger["Transfer:".Length..].Split("->", StringSplitOptions.None);
            if (route.Length != 2)
            {
                return (false, normalizedCategory, normalizedLedger,
                    "Transfer ledger category must use the format Transfer:Source->Target.");
            }

            var source = FinancialConstants.BudgetCategories.FirstOrDefault(bucket =>
                bucket.Equals(route[0].Trim(), StringComparison.OrdinalIgnoreCase));
            var target = FinancialConstants.BudgetCategories.FirstOrDefault(bucket =>
                bucket.Equals(route[1].Trim(), StringComparison.OrdinalIgnoreCase));

            if (source == null || target == null)
            {
                return (false, normalizedCategory, normalizedLedger,
                    $"Transfer source and target must be one of: {string.Join(", ", FinancialConstants.BudgetCategories)}.");
            }
            if (source == target)
            {
                return (false, normalizedCategory, normalizedLedger,
                    "Transfer source and target must be different.");
            }
            if (amount <= 0)
            {
                return (false, normalizedCategory, normalizedLedger,
                    "Transfer amount must be greater than zero.");
            }

            return (true, "Transfer", $"Transfer:{source}->{target}", null);
        }

        if (isTransferCategory)
        {
            return (false, normalizedCategory, normalizedLedger,
                "The Transfer category requires a valid Transfer:Source->Target ledger route.");
        }

        if (normalizedLedger.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = normalizedLedger["IncomeSplit:".Length..].Split(',');
            var percentages = new decimal[4];
            var validPercentages = parts.Length == 4;
            for (var i = 0; validPercentages && i < parts.Length; i++)
            {
                validPercentages = decimal.TryParse(
                    parts[i],
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out percentages[i]) && percentages[i] >= 0;
            }
            if (!validPercentages || Math.Abs(percentages.Sum() - 100m) > 0.01m)
            {
                return (false, normalizedCategory, normalizedLedger,
                    "Income split must contain four non-negative percentages totaling 100.");
            }
            if (amount <= 0)
            {
                return (false, normalizedCategory, normalizedLedger,
                    "Income amount must be greater than zero.");
            }

            return (true, normalizedCategory,
                $"IncomeSplit:{string.Join(',', percentages.Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)))}", null);
        }

        var plainLedger = new[] { "Income", "Discarded" }
            .Concat(FinancialConstants.BudgetCategories)
            .FirstOrDefault(value => value.Equals(normalizedLedger, StringComparison.OrdinalIgnoreCase));
        if (plainLedger == null)
        {
            return (false, normalizedCategory, normalizedLedger, "Ledger category is not recognized.");
        }
        if (plainLedger == "Income" && amount <= 0)
        {
            return (false, normalizedCategory, normalizedLedger, "Income amount must be greater than zero.");
        }
        if (plainLedger == "Discarded" && amount != 0)
        {
            return (false, normalizedCategory, normalizedLedger, "Discarded transactions must have a zero amount.");
        }

        return (true, normalizedCategory, plainLedger, null);
    }
}
