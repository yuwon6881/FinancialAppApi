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
    private readonly Stability.StabilityRecoveryService _stabilityRecoveryService;

    public TransactionPersistenceService(
        AppDbContext context,
        CycleBalanceService cycleBalanceService,
        RecurringOccurrenceService occurrenceService,
        Stability.StabilityRecoveryService stabilityRecoveryService)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService;
        _occurrenceService = occurrenceService;
        _stabilityRecoveryService = stabilityRecoveryService;
    }

    public async Task<TransactionMutationResult> CreateTransactionAsync(
        TransactionMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(request.Id))
        {
            var existingTx = await _context.Transactions
                .FirstOrDefaultAsync(t => t.Id == request.Id, cancellationToken);
            if (existingTx != null)
            {
                return new TransactionMutationResult(TransactionMutationStatus.Existing, existingTx);
            }
        }

        if (!await TransactionCategoryExistsAsync(request.Category, cancellationToken))
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
        var occurrence = await ResolveRecurringOccurrenceDateAsync(
            transaction,
            request.RecurringOccurrenceDate,
            cancellationToken);
        if (!occurrence.IsValid)
        {
            return new TransactionMutationResult(TransactionMutationStatus.InvalidRecurringOccurrence, Message: occurrence.Message);
        }
        transaction.RecurringOccurrenceDate = occurrence.Date;

        var splitContext = await LoadIncomeSplitContextAsync(
            transaction.LedgerCategory, transaction.Id, cancellationToken);
        var splitSpec = ResolveIncomeSplitSpec(transaction, splitContext);

        _context.Transactions.Add(transaction);
        AddIncomeSplitTransactions(transaction, splitSpec);
        await ApplyWishlistPurchaseLinkAsync(transaction, cancellationToken);

        try
        {
            await SaveAndInvalidateCycleBalancesAsync(transaction.Date, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation() && transaction.RecurringOccurrenceDate != null)
        {
            return new TransactionMutationResult(TransactionMutationStatus.Conflict, Message: "This recurring occurrence has already been settled.");
        }

        return new TransactionMutationResult(TransactionMutationStatus.Created, transaction);
    }

    public async Task<TransactionMutationResult> UpdateTransactionAsync(
        string id,
        TransactionMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        var transaction = await _context.Transactions.FindAsync([id], cancellationToken);
        if (transaction == null)
        {
            return new TransactionMutationResult(TransactionMutationStatus.NotFound);
        }

        // Completion entries carry a snapshot whose amount/date must remain exact for deletion to
        // reverse the commitment safely. Let users delete (undo) them, but never edit them into a
        // ledger row that no longer matches the saved goal transition.
        if (transaction.SavingsGoalId.HasValue)
        {
            return new TransactionMutationResult(
                TransactionMutationStatus.Conflict,
                transaction,
                "A commitment completion cannot be edited. Delete it to restore the commitment, then complete it again.");
        }

        if (!await TransactionCategoryExistsAsync(request.Category, cancellationToken))
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

        // Before any staging below: see LoadIncomeSplitContextAsync on why its I/O cannot run once
        // the change tracker is dirty.
        var splitContext = await LoadIncomeSplitContextAsync(
            ledgerValidation.LedgerCategory, transaction.Id, cancellationToken);

        var existingSplits = await _context.Transactions
            .Where(t => t.Id.StartsWith(transaction.Id + "-split-"))
            .ToListAsync(cancellationToken);
        _context.Transactions.RemoveRange(existingSplits);

        transaction.Date = TransactionDate.PreserveTimeWhenSameDate(transaction.Date, putDate);
        transaction.Description = request.Description;
        transaction.Category = ledgerValidation.Category;
        transaction.LedgerCategory = ledgerValidation.LedgerCategory;
        transaction.Amount = amount;
        transaction.RecurringPaymentId = request.RecurringPaymentId ?? transaction.RecurringPaymentId;
        transaction.WishlistItemId = request.WishlistItemId ?? transaction.WishlistItemId;

        var splitSpec = ResolveIncomeSplitSpec(transaction, splitContext);
        AddIncomeSplitTransactions(transaction, splitSpec);
        await ApplyWishlistPurchaseLinkAsync(transaction, cancellationToken);

        await SaveAndInvalidateCycleBalancesAsync(
            originalDate < transaction.Date ? originalDate : transaction.Date,
            cancellationToken);
        return new TransactionMutationResult(TransactionMutationStatus.Updated, transaction);
    }

    public async Task<TransactionMutationResult> DeleteTransactionAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var transaction = await _context.Transactions.FindAsync([id], cancellationToken);
        if (transaction == null)
        {
            return new TransactionMutationResult(TransactionMutationStatus.NotFound);
        }

        var goalRestoreError = await RestoreSavingsGoalCompletionAsync(transaction, cancellationToken);
        if (goalRestoreError != null)
        {
            return new TransactionMutationResult(TransactionMutationStatus.Conflict, transaction, goalRestoreError);
        }

        await ClearWishlistPurchaseLinkAsync(transaction, cancellationToken);

        var attachedDocuments = await _context.VaultDocuments
            .Where(document => document.TransactionId == id)
            .ToListAsync(cancellationToken);
        foreach (var document in attachedDocuments)
        {
            document.TransactionId = null;
        }

        var splits = await _context.Transactions
            .Where(t => t.Id.StartsWith(id + "-split-"))
            .ToListAsync(cancellationToken);
        _context.Transactions.RemoveRange(splits);

        _context.Transactions.Remove(transaction);
        try
        {
            await SaveAndInvalidateCycleBalancesAsync(transaction.Date, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new TransactionMutationResult(
                TransactionMutationStatus.Conflict,
                transaction,
                "The linked commitment changed while this completion was being deleted. Refresh and try again.");
        }

        return new TransactionMutationResult(TransactionMutationStatus.Deleted, transaction);
    }

    // Tags a new transaction with the exact recurrence-engine billing date it settles, when it
    // was created against a recurring payment and its date lines up with that payment's cycle
    // occurrence. Legacy/manual transactions (no match, or no RecurringPaymentId) keep this null.
    private async Task<(bool IsValid, DateOnly? Date, string? Message)> ResolveRecurringOccurrenceDateAsync(
        Transaction transaction,
        string? requestedOccurrenceDate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(transaction.RecurringPaymentId))
        {
            return string.IsNullOrWhiteSpace(requestedOccurrenceDate)
                ? (true, null, null)
                : (false, null, "A recurring occurrence requires a recurring payment.");
        }

        var payment = await _context.RecurringPayments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == transaction.RecurringPaymentId, cancellationToken);
        if (payment == null)
        {
            return string.IsNullOrWhiteSpace(requestedOccurrenceDate)
                ? (true, null, null)
                : (false, null, "The recurring payment could not be found.");
        }

        var setting = await _context.FinancialSettings.AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        var cycleDay = setting?.CycleDay ?? FinancialConstants.DefaultCycleDay;

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
            t.RecurringPaymentId == payment.Id && t.RecurringOccurrenceDate == matchedOccurrence,
            cancellationToken);

        return alreadySettled
            ? (false, null, "This recurring occurrence has already been settled.")
            : (true, matchedOccurrence, null);
    }

    private async Task InvalidateCycleBalancesFromAsync(
        DateTime date,
        CancellationToken cancellationToken)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        if (setting == null) return;

        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(TransactionDate.ToDateOnly(date), setting.CycleDay);
        await _cycleBalanceService.InvalidateFromAsync(year, monthIndex, cancellationToken);
    }

    private async Task SaveAndInvalidateCycleBalancesAsync(
        DateTime earliestAffectedDate,
        CancellationToken cancellationToken)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var dbTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await InvalidateCycleBalancesFromAsync(earliestAffectedDate, cancellationToken);
            await dbTransaction.CommitAsync(cancellationToken);
        });
    }

    /// <summary>
    /// The settings and fund balance an income split is resolved against.
    /// </summary>
    private sealed record IncomeSplitContext(FinancialSetting? Setting, Stability.StabilityState? State);

    private static bool IsIncomeLedgerCategory(string? ledgerCategory) =>
        string.Equals(ledgerCategory, "Income", StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrEmpty(ledgerCategory)
            && ledgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Loads what an income split needs, doing all of its I/O up front.
    /// <para>
    /// Callers MUST invoke this before staging any change on the context. Reading the fund balance
    /// goes through <c>GetOpeningBalanceAsync</c>, which can rebuild the cycle-balance cache and
    /// call <c>SaveChangesAsync</c> -- with edits already staged that would commit them early, and
    /// outside the transaction <c>SaveAndInvalidateCycleBalancesAsync</c> opens.
    /// </para>
    /// </summary>
    private async Task<IncomeSplitContext> LoadIncomeSplitContextAsync(
        string? ledgerCategory,
        string transactionId,
        CancellationToken cancellationToken)
    {
        if (!IsIncomeLedgerCategory(ledgerCategory)) return new IncomeSplitContext(null, null);

        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        if (setting == null) return new IncomeSplitContext(null, null);

        // Excludes this transaction's own earlier contribution, so re-saving a salary is measured
        // against the fund without itself rather than on top of it.
        var state = await _stabilityRecoveryService.GetStabilityStateAsync(
            setting, transactionId, cancellationToken);
        return new IncomeSplitContext(setting, state);
    }

    private static string ResolveIncomeSplitSpec(Transaction transaction, IncomeSplitContext context)
    {
        var isPlainIncome = string.Equals(transaction.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase);
        var isProposedSplit = !string.IsNullOrEmpty(transaction.LedgerCategory)
            && transaction.LedgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase);
        if (!isPlainIncome && !isProposedSplit) return "";

        var (setting, state) = context;

        // Plain "Income" is the only branch that needs settings -- it has no percentages of its own
        // to fall back on. A proposed split carries its own and must still save without a settings
        // row, which is how it behaved before the cap moved server-side.
        if (setting == null && isPlainIncome) return "";

        if (isPlainIncome)
        {
            // Every caller gets the target cap here, not just the web form. It used to be enforced
            // client-side only, so AI ledger drafts, recurring settlement and any outbox replay
            // whose balance had gone stale applied raw percentages and sailed past the target.
            return Stability.IncomeSplitPlanner.Resolve(
                transaction.Amount,
                setting!.EssentialsAlloc,
                setting.GrowthAlloc,
                setting.StabilityAlloc,
                setting.RewardsAlloc,
                state!.CurrentBalance,
                state.Target,
                setting.StabilityOverflowRedirect,
                requestedTopUp: 0m).ToSpecString();
        }

        var proposedSpec = transaction.LedgerCategory!["IncomeSplit:".Length..];
        transaction.LedgerCategory = "Income";
        if (!Stability.IncomeSplitSpec.TryParseSpecString(proposedSpec, out var proposed))
        {
            return "";
        }

        // Clamped, never rejected: a proposal can arrive from an offline replay whose balance moved
        // before the queue drained, and rejecting it would fail a salary the user cannot re-enter.
        // The excess lands wherever StabilityOverflowRedirect points, which is that setting's job.
        var (spec, _) = Stability.IncomeSplitPlanner.ClampProposed(
            proposed,
            transaction.Amount,
            state?.CurrentBalance ?? 0m,
            state?.Target ?? 0m,
            setting?.StabilityOverflowRedirect);
        return spec.ToSpecString();
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

    private async Task ApplyWishlistPurchaseLinkAsync(
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        if (!transaction.WishlistItemId.HasValue) return;

        var item = await _context.WishlistItems.FindAsync(
            [transaction.WishlistItemId.Value],
            cancellationToken);
        if (item == null) return;

        item.IsPurchased = true;
        item.PurchasedAt = transaction.Date;
        item.PurchaseTransactionId = transaction.Id;
        item.IsActive = false;
    }

    private async Task ClearWishlistPurchaseLinkAsync(
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        WishlistItem? item = null;

        if (transaction.WishlistItemId.HasValue)
        {
            item = await _context.WishlistItems.FindAsync(
                [transaction.WishlistItemId.Value],
                cancellationToken);
        }

        item ??= await _context.WishlistItems
            .FirstOrDefaultAsync(
                w => w.PurchaseTransactionId == transaction.Id,
                cancellationToken);

        if (item == null) return;

        item.IsPurchased = false;
        item.PurchasedAt = null;
        item.PurchaseTransactionId = null;

        var hasActiveUnpurchased = await _context.WishlistItems
            .AnyAsync(
                w => w.Id != item.Id && !w.IsPurchased && w.IsActive,
                cancellationToken);
        if (!hasActiveUnpurchased)
        {
            item.IsActive = true;
        }
    }

    private async Task<string?> RestoreSavingsGoalCompletionAsync(
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        if (!transaction.SavingsGoalId.HasValue) return null;

        var completion = await _context.SavingsGoalCompletions
            .FirstOrDefaultAsync(item => item.TransactionId == transaction.Id, cancellationToken);
        if (completion == null) return null;

        var goal = await _context.SavingsGoals.FindAsync([completion.SavingsGoalId], cancellationToken);
        if (goal != null)
        {
            var expectedStatus = completion.WasRecurring
                ? SavingsGoalStatus.Active
                : SavingsGoalStatus.Completed;
            var isUntouchedLatestCompletion =
                goal.LastCompletionTransactionId == transaction.Id &&
                goal.Status == expectedStatus &&
                goal.TargetDate.Date == completion.ResultingTargetDate.Date &&
                goal.EarmarkedAmount == 0m &&
                goal.CycleFundedKey == null &&
                goal.CycleFundedAmount == 0m;

            if (!isUntouchedLatestCompletion)
            {
                return "This commitment changed after it was completed. Undo its newer changes before deleting this completion entry.";
            }

            goal.TargetDate = completion.PreviousTargetDate;
            goal.EarmarkedAmount = completion.PreviousEarmarkedAmount;
            goal.CycleFundedKey = completion.PreviousCycleFundedKey;
            goal.CycleFundedAmount = completion.PreviousCycleFundedAmount;
            goal.Status = SavingsGoalStatus.Active;
            goal.CompletedAt = null;
            goal.LastCompletionTransactionId = null;
        }

        _context.SavingsGoalCompletions.Remove(completion);
        return null;
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

    private async Task<bool> TransactionCategoryExistsAsync(
        string category,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return false;
        }

        if (TransactionCategoryService.IsReservedName(category))
        {
            return true;
        }

        return await _context.TransactionCategories.AnyAsync(
            c => c.Name.ToLower() == category.Trim().ToLower(),
            cancellationToken);
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
