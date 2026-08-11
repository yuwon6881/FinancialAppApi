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
    string? RecurringOccurrenceDate = null,
    string? StabilityRecoveryTopUpAmount = null,
    string? StabilityReloadIntent = null);

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
    private readonly RecurringOccurrenceLedgerService _occurrenceLedger;
    private readonly FinancialClock _clock;

    public TransactionPersistenceService(
        AppDbContext context,
        CycleBalanceService cycleBalanceService,
        RecurringOccurrenceService occurrenceService,
        Stability.StabilityRecoveryService stabilityRecoveryService,
        RecurringOccurrenceLedgerService? occurrenceLedger = null,
        FinancialClock? clock = null)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService;
        _occurrenceService = occurrenceService;
        _stabilityRecoveryService = stabilityRecoveryService;
        _clock = clock ?? FinancialClock.Utc;
        _occurrenceLedger = occurrenceLedger ?? new RecurringOccurrenceLedgerService(context, occurrenceService, _clock);
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
        if (!TryDecodeRecoveryTopUp(request.StabilityRecoveryTopUpAmount, out var requestedRecoveryTopUp))
            return InvalidAmount("The Stability reimbursement amount is invalid.");
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
            StabilityRecoveryTopUpAmount = IsIncomeLedgerCategory(ledgerValidation.LedgerCategory)
                ? requestedRecoveryTopUp
                : null,
            StabilityReloadIntent = StabilityReloadIntent.Normalize(request.StabilityReloadIntent),
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
        RecurringPaymentOccurrence? occurrenceRow = null;
        if (occurrence.Date.HasValue)
        {
            if (postDate > _clock.Today)
            {
                return new TransactionMutationResult(
                    TransactionMutationStatus.InvalidDate,
                    Message: "A recurring payment date cannot be in the future.");
            }
            var payment = await _context.RecurringPayments.FirstAsync(
                candidate => candidate.Id == transaction.RecurringPaymentId,
                cancellationToken);
            if (occurrence.Date.Value > _clock.Today && payment.PaymentMode == RecurringPaymentMode.AutoDeduct)
            {
                return new TransactionMutationResult(
                    TransactionMutationStatus.InvalidRecurringOccurrence,
                    Message: "An automatically deducted bill cannot be settled before its due date.");
            }
            try
            {
                occurrenceRow = await _occurrenceLedger.EnsureOccurrenceAsync(payment, occurrence.Date.Value, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                return new TransactionMutationResult(
                    TransactionMutationStatus.InvalidRecurringOccurrence,
                    Message: exception.Message);
            }
            if (occurrenceRow.Status != RecurringOccurrenceStatus.Pending)
            {
                return new TransactionMutationResult(
                    TransactionMutationStatus.Conflict,
                    Message: "This recurring occurrence has already been settled.");
            }
        }

        var splitContext = await LoadIncomeSplitContextAsync(
            transaction.LedgerCategory, TransactionDate.ToDateOnly(transaction.Date), transaction.Id,
            preserveHistoricalRecovery: false, cancellationToken: cancellationToken);
        var splitSpec = ResolveIncomeSplitSpec(transaction, splitContext);

        // Read before anything is staged, for the same reason LoadIncomeSplitContextAsync is.
        var documentsToRelink = await _context.VaultDocuments
            .Where(document => document.DetachedFromTransactionId == transaction.Id
                && document.TransactionId == null)
            .ToListAsync(cancellationToken);

        _context.Transactions.Add(transaction);
        if (occurrenceRow != null) RecurringOccurrenceLedgerService.SettleFromTransaction(occurrenceRow, transaction);
        foreach (var document in documentsToRelink)
        {
            document.TransactionId = transaction.Id;
            document.DetachedFromTransactionId = null;
        }
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
        if (!TryDecodeRecoveryTopUp(request.StabilityRecoveryTopUpAmount, out var requestedRecoveryTopUp))
            return InvalidAmount("The Stability reimbursement amount is invalid.");
        var ledgerValidation = ValidateAndNormalizeLedgerCategory(request.Category, request.LedgerCategory, amount);
        if (!ledgerValidation.IsValid)
        {
            return InvalidLedgerCategory(ledgerValidation.Message!);
        }

        var originalDate = transaction.Date;
        var originalAmount = transaction.Amount;
        var originalRecoveryTopUp = transaction.StabilityRecoveryTopUpAmount;
        var originalReloadIntent = transaction.StabilityReloadIntent;
        var nextRecoveryTopUp = request.StabilityRecoveryTopUpAmount == null
            ? originalRecoveryTopUp
            : requestedRecoveryTopUp;
        if (!IsIncomeLedgerCategory(ledgerValidation.LedgerCategory)) nextRecoveryTopUp = null;
        var settingForCycle = await _context.FinancialSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (originalRecoveryTopUp > 0m && nextRecoveryTopUp > 0m && settingForCycle != null)
        {
            var originalCycle = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
                TransactionDate.ToDateOnly(originalDate), settingForCycle.CycleDay);
            var nextCycle = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(putDate, settingForCycle.CycleDay);
            if (originalCycle != nextCycle)
            {
                return new TransactionMutationResult(
                    TransactionMutationStatus.Conflict,
                    transaction,
                    "Remove the Stability reimbursement before moving this salary to another cycle.");
            }
        }

        // Before any staging below: see LoadIncomeSplitContextAsync on why its I/O cannot run once
        // the change tracker is dirty.
        var currentCycle = settingForCycle == null
            ? default
            : CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
                _clock.Today, settingForCycle.CycleDay);
        var originalTransactionCycle = settingForCycle == null
            ? default
            : CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
                TransactionDate.ToDateOnly(originalDate), settingForCycle.CycleDay);
        var preserveHistoricalRecovery = settingForCycle != null
            && originalRecoveryTopUp > 0m
            && nextRecoveryTopUp > 0m
            && originalAmount == amount
            && originalTransactionCycle != currentCycle;
        var splitContext = await LoadIncomeSplitContextAsync(
            ledgerValidation.LedgerCategory, putDate, transaction.Id,
            preserveHistoricalRecovery, cancellationToken: cancellationToken);

        var existingSplits = await _context.Transactions
            .Where(t => t.Id.StartsWith(transaction.Id + "-split-"))
            .ToListAsync(cancellationToken);
        _context.Transactions.RemoveRange(existingSplits);

        transaction.Date = TransactionDate.PreserveTimeWhenSameDate(transaction.Date, putDate);
        transaction.Description = request.Description;
        transaction.Category = ledgerValidation.Category;
        transaction.LedgerCategory = ledgerValidation.LedgerCategory;
        transaction.Amount = amount;
        transaction.StabilityRecoveryTopUpAmount = nextRecoveryTopUp;
        transaction.StabilityReloadIntent = request.StabilityReloadIntent == null
            ? originalReloadIntent
            : StabilityReloadIntent.Normalize(request.StabilityReloadIntent);
        transaction.RecurringPaymentId = request.RecurringPaymentId ?? transaction.RecurringPaymentId;
        transaction.WishlistItemId = request.WishlistItemId ?? transaction.WishlistItemId;

        if (transaction.RecurringOccurrenceDate.HasValue)
        {
            if (putDate > _clock.Today) return InvalidDate("A recurring payment date cannot be in the future.");
            var occurrenceRow = await _occurrenceLedger.FindByTransactionAsync(transaction, cancellationToken);
            if (occurrenceRow != null) RecurringOccurrenceLedgerService.SettleFromTransaction(occurrenceRow, transaction);
        }

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

        var recurringOccurrence = await _occurrenceLedger.FindByTransactionAsync(transaction, cancellationToken);
        if (recurringOccurrence != null && recurringOccurrence.SettlementTransactionId == transaction.Id)
        {
            RecurringOccurrenceLedgerService.Reopen(recurringOccurrence);
        }

        var attachedDocuments = await _context.VaultDocuments
            .Where(document => document.TransactionId == id)
            .ToListAsync(cancellationToken);
        foreach (var document in attachedDocuments)
        {
            document.TransactionId = null;
            document.DetachedFromTransactionId = id;
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

    public async Task<(TransactionMutationStatus Status, List<Transaction> Transactions, string? Message)> DeleteTransactionsAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken = default)
    {
        var canonicalIds = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => CanonicalTransactionId(id.Trim()))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (canonicalIds.Count == 0)
        {
            return (TransactionMutationStatus.NotFound, [], "Choose at least one transaction.");
        }
        if (canonicalIds.Count > 100)
        {
            return (TransactionMutationStatus.InvalidAmount, [], "Delete at most 100 transactions at a time.");
        }

        var transactions = await _context.Transactions
            .Where(transaction => canonicalIds.Contains(transaction.Id))
            .ToListAsync(cancellationToken);
        var protectedTransaction = transactions.FirstOrDefault(transaction => transaction.SavingsGoalId.HasValue);
        if (protectedTransaction != null)
        {
            return (
                TransactionMutationStatus.Conflict,
                [],
                "Commitment completions must be deleted individually so the linked commitment can be restored safely.");
        }

        if (transactions.Count == 0) return (TransactionMutationStatus.Deleted, [], null);

        var earliestDate = transactions.Min(transaction => transaction.Date);
        foreach (var transaction in transactions)
        {
            await ClearWishlistPurchaseLinkAsync(transaction, cancellationToken);

            var recurringOccurrence = await _occurrenceLedger.FindByTransactionAsync(transaction, cancellationToken);
            if (recurringOccurrence != null && recurringOccurrence.SettlementTransactionId == transaction.Id)
            {
                RecurringOccurrenceLedgerService.Reopen(recurringOccurrence);
            }

            var attachedDocuments = await _context.VaultDocuments
                .Where(document => document.TransactionId == transaction.Id)
                .ToListAsync(cancellationToken);
            foreach (var document in attachedDocuments)
            {
                document.TransactionId = null;
                document.DetachedFromTransactionId = transaction.Id;
            }

            var splits = await _context.Transactions
                .Where(candidate => candidate.Id.StartsWith(transaction.Id + "-split-"))
                .ToListAsync(cancellationToken);
            _context.Transactions.RemoveRange(splits);
            _context.Transactions.Remove(transaction);
        }

        await SaveAndInvalidateCycleBalancesAsync(earliestDate, cancellationToken);
        return (TransactionMutationStatus.Deleted, transactions, null);
    }

    public async Task<(TransactionMutationStatus Status, List<Transaction> Transactions, string? Message)> RestoreTransactionsAsync(
        IReadOnlyCollection<TransactionMutationRequest> requests,
        CancellationToken cancellationToken = default)
    {
        var distinctRequests = new List<TransactionMutationRequest>();
        var requestsById = new Dictionary<string, TransactionMutationRequest>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            if (string.IsNullOrWhiteSpace(request.Id))
            {
                return (TransactionMutationStatus.InvalidDate, [], "Every transaction to restore needs an id.");
            }

            var id = request.Id.Trim();
            if (id.Contains("-split-", StringComparison.Ordinal))
            {
                return (TransactionMutationStatus.Conflict, [], "Restore the parent transaction, not an Income Auto-Split row.");
            }

            var normalizedRequest = request with { Id = id };
            if (requestsById.TryGetValue(id, out var previous))
            {
                if (!MatchesRestoreRequests(previous, normalizedRequest))
                {
                    return (TransactionMutationStatus.Conflict, [], "The restore batch contains conflicting snapshots for one transaction.");
                }
                continue;
            }

            requestsById[id] = normalizedRequest;
            distinctRequests.Add(normalizedRequest);
        }
        if (distinctRequests.Count == 0)
        {
            return (TransactionMutationStatus.NotFound, [], "Choose at least one transaction to restore.");
        }
        if (distinctRequests.Count > 100)
        {
            return (TransactionMutationStatus.InvalidAmount, [], "Restore at most 100 transactions at a time.");
        }

        var existingTransactions = await _context.Transactions
            .Where(transaction => requestsById.Keys.Contains(transaction.Id))
            .ToDictionaryAsync(transaction => transaction.Id, StringComparer.Ordinal, cancellationToken);
        foreach (var request in distinctRequests)
        {
            if (existingTransactions.TryGetValue(request.Id!, out var existing)
                && !MatchesRestoreRequest(existing, request))
            {
                return (TransactionMutationStatus.Conflict, [], "A transaction with one of these ids already exists with different details.");
            }
        }

        var restored = new List<Transaction>();
        var sawCreated = false;
        foreach (var request in distinctRequests)
        {
            var result = await CreateTransactionAsync(request, cancellationToken);
            if (result.Status is not (TransactionMutationStatus.Created or TransactionMutationStatus.Existing))
            {
                return (result.Status, [], result.Message);
            }
            sawCreated |= result.Status == TransactionMutationStatus.Created;
            if (result.Transaction != null) restored.Add(result.Transaction);
        }
        return (sawCreated ? TransactionMutationStatus.Created : TransactionMutationStatus.Existing, restored, null);
    }

    private static string CanonicalTransactionId(string id)
    {
        var splitIndex = id.IndexOf("-split-", StringComparison.Ordinal);
        return splitIndex < 0 ? id : id[..splitIndex];
    }

    private static bool MatchesRestoreRequests(
        TransactionMutationRequest left,
        TransactionMutationRequest right) =>
        string.Equals(left.Date, right.Date, StringComparison.Ordinal)
        && string.Equals(left.PostedAt, right.PostedAt, StringComparison.Ordinal)
        && string.Equals(left.Description, right.Description, StringComparison.Ordinal)
        && string.Equals(left.Category, right.Category, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.LedgerCategory, right.LedgerCategory, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Amount, right.Amount, StringComparison.Ordinal)
        && string.Equals(left.RecurringPaymentId, right.RecurringPaymentId, StringComparison.Ordinal)
        && left.WishlistItemId == right.WishlistItemId
        && string.Equals(left.RecurringOccurrenceDate, right.RecurringOccurrenceDate, StringComparison.Ordinal)
        && string.Equals(left.StabilityRecoveryTopUpAmount, right.StabilityRecoveryTopUpAmount, StringComparison.Ordinal)
        && string.Equals(
            StabilityReloadIntent.Normalize(left.StabilityReloadIntent),
            StabilityReloadIntent.Normalize(right.StabilityReloadIntent),
            StringComparison.Ordinal);

    private static bool MatchesRestoreRequest(
        Transaction existing,
        TransactionMutationRequest request)
    {
        if (!TransactionDate.TryParseInputDate(request.Date, out var requestDate)
            || !ObfuscationHelper.TryDeobfuscate(request.Amount, out var decodedAmount)
            || !TryDecodeRecoveryTopUp(request.StabilityRecoveryTopUpAmount, out var recoveryTopUp))
        {
            return false;
        }

        var requestOccurrence = string.IsNullOrWhiteSpace(request.RecurringOccurrenceDate)
            ? (DateOnly?)null
            : DateOnly.TryParseExact(request.RecurringOccurrenceDate, "yyyy-MM-dd", out var parsedOccurrence)
                ? parsedOccurrence
                : null;
        if (!string.IsNullOrWhiteSpace(request.RecurringOccurrenceDate) && requestOccurrence == null)
        {
            return false;
        }

        var postedAtMatches = string.IsNullOrWhiteSpace(request.PostedAt)
            || (DateTimeOffset.TryParse(request.PostedAt, out var postedAt)
                && existing.PostedAt.ToUniversalTime() == postedAt.UtcDateTime);
        var comparableRecoveryTopUp = recoveryTopUp;
        if (comparableRecoveryTopUp == null
            && string.Equals(request.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase))
        {
            // New ordinary salaries persist explicit zero, while an older Undo snapshot can omit
            // the field. They describe the same unticked choice and must remain idempotent.
            comparableRecoveryTopUp = 0m;
        }
        return TransactionDate.ToDateOnly(existing.Date) == requestDate
            && postedAtMatches
            && string.Equals(existing.Description, request.Description, StringComparison.Ordinal)
            && string.Equals(existing.Category, request.Category, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.LedgerCategory, request.LedgerCategory, StringComparison.OrdinalIgnoreCase)
            && existing.Amount == Math.Round(decodedAmount, 2, MidpointRounding.AwayFromZero)
            && string.Equals(existing.RecurringPaymentId, request.RecurringPaymentId, StringComparison.Ordinal)
            && existing.WishlistItemId == request.WishlistItemId
            && existing.RecurringOccurrenceDate == requestOccurrence
            && existing.StabilityRecoveryTopUpAmount == comparableRecoveryTopUp
            && string.Equals(
                existing.StabilityReloadIntent,
                StabilityReloadIntent.Normalize(request.StabilityReloadIntent),
                StringComparison.Ordinal);
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
    private sealed record IncomeSplitContext(
        FinancialSetting? Setting,
        Stability.StabilityState? State,
        bool PreserveHistoricalRecovery = false);

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
        DateOnly transactionDate,
        string transactionId,
        bool preserveHistoricalRecovery,
        CancellationToken cancellationToken)
    {
        if (!IsIncomeLedgerCategory(ledgerCategory)) return new IncomeSplitContext(null, null);

        var setting = await _context.FinancialSettings.FirstOrDefaultAsync(cancellationToken);
        if (setting == null) return new IncomeSplitContext(null, null);

        // Excludes this transaction's own earlier contribution, so re-saving a salary is measured
        // against the fund without itself rather than on top of it.
        var state = await _stabilityRecoveryService.GetStabilityStateAsync(
            setting, transactionDate, transactionId, cancellationToken);
        return new IncomeSplitContext(setting, state, preserveHistoricalRecovery);
    }

    private static string ResolveIncomeSplitSpec(Transaction transaction, IncomeSplitContext context)
    {
        var isPlainIncome = string.Equals(transaction.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase);
        var isProposedSplit = !string.IsNullOrEmpty(transaction.LedgerCategory)
            && transaction.LedgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase);
        if (!isPlainIncome && !isProposedSplit) return "";

        var setting = context.Setting;
        var state = context.State;

        // Plain "Income" is the only branch that needs settings -- it has no percentages of its own
        // to fall back on. A proposed split carries its own and must still save without a settings
        // row, which is how it behaved before the cap moved server-side.
        if (setting == null && isPlainIncome) return "";

        if (isPlainIncome)
        {
            // Every caller gets the target cap here, not just the web form. It used to be enforced
            // client-side only, so AI ledger drafts, recurring settlement and any outbox replay
            // whose balance had gone stale applied raw percentages and sailed past the target.
            var requestedTopUp = Math.Max(0m, transaction.StabilityRecoveryTopUpAmount ?? 0m);
            if (context.PreserveHistoricalRecovery && requestedTopUp > 0m)
            {
                return Stability.IncomeSplitPlanner.Resolve(
                    transaction.Amount,
                    setting!.EssentialsAlloc,
                    setting.GrowthAlloc,
                    setting.StabilityAlloc,
                    setting.RewardsAlloc,
                    stabilityBalance: 0m,
                    stabilityTarget: 0m,
                    stabilityOverflowRedirect: setting.StabilityOverflowRedirect,
                    requestedTopUp: requestedTopUp).ToSpecString();
            }
            var maximumTopUp = MaximumRecoveryTopUp(transaction.Amount, setting!, state!);
            var appliedTopUp = Math.Min(requestedTopUp, maximumTopUp);
            // New salaries always record the explicit answer, including zero. Leaving ordinary
            // salary as legacy-null lets a later allocation change reinterpret it as repayment.
            transaction.StabilityRecoveryTopUpAmount = appliedTopUp;
            return Stability.IncomeSplitPlanner.Resolve(
                transaction.Amount,
                setting!.EssentialsAlloc,
                setting.GrowthAlloc,
                setting.StabilityAlloc,
                setting.RewardsAlloc,
                state!.CurrentBalance,
                state.Target,
                setting.StabilityOverflowRedirect,
                requestedTopUp: appliedTopUp).ToSpecString();
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
        if (setting == null || state == null)
        {
            var (fallback, _) = Stability.IncomeSplitPlanner.ClampProposed(
                proposed, transaction.Amount, 0m, 0m, setting?.StabilityOverflowRedirect);
            return fallback.ToSpecString();
        }

        var baselineStability = Math.Max(0m, setting.StabilityAlloc);
        var proposedTopUp = transaction.StabilityRecoveryTopUpAmount
            ?? Math.Max(0m, transaction.Amount * (proposed.Stability - baselineStability));
        var applied = Math.Min(proposedTopUp, MaximumRecoveryTopUp(transaction.Amount, setting, state));
        transaction.StabilityRecoveryTopUpAmount = applied;
        return Stability.IncomeSplitPlanner.Resolve(
            transaction.Amount,
            setting.EssentialsAlloc,
            setting.GrowthAlloc,
            setting.StabilityAlloc,
            setting.RewardsAlloc,
            state.CurrentBalance,
            state.Target,
            setting.StabilityOverflowRedirect,
            applied).ToSpecString();
    }

    private static decimal MaximumRecoveryTopUp(
        decimal incomeAmount,
        FinancialSetting setting,
        Stability.StabilityState state)
    {
        if (incomeAmount <= 0m) return 0m;
        var normalStability = incomeAmount * Math.Max(0m, setting.StabilityAlloc);
        var targetRoomAfterNormal = state.Target > 0m
            ? Math.Max(0m, state.Target - state.CurrentBalance - normalStability)
            : decimal.MaxValue;
        var otherShare = Math.Max(0m,
            setting.EssentialsAlloc + setting.GrowthAlloc + setting.RewardsAlloc);
        var capacity = Math.Min(
            state.OutstandingObligation,
            Math.Min(targetRoomAfterNormal, incomeAmount * otherShare));
        return Math.Floor(Math.Max(0m, capacity) * 100m) / 100m;
    }

    private static bool TryDecodeRecoveryTopUp(string? encoded, out decimal? value)
    {
        value = null;
        if (encoded == null) return true;
        if (!ObfuscationHelper.TryDeobfuscate(encoded, out var decoded) || decoded < 0m) return false;
        value = Math.Round(decoded, 2, MidpointRounding.AwayFromZero);
        return true;
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

    private static TransactionMutationResult InvalidDate(string? message = null)
    {
        return new TransactionMutationResult(
            TransactionMutationStatus.InvalidDate,
            Message: message ?? "Date must be in yyyy-MM-dd format.");
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

    private static TransactionMutationResult InvalidAmount(string message = "Amount is malformed.") => new(
        TransactionMutationStatus.InvalidAmount,
        Message: message);

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
