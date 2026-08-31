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
    InvalidAccount,
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
    string? StabilityReloadIntent = null,
    string? AccountId = null,
    string? CounterAccountId = null,
    IReadOnlyDictionary<string, string>? SplitAccountIds = null);

public sealed record TransactionMutationResult(
    TransactionMutationStatus Status,
    Transaction? Transaction = null,
    string? Message = null,
    string? Code = null,
    IReadOnlyList<string>? MissingBuckets = null);

public partial class TransactionPersistenceService
{
    private readonly AppDbContext _context;
    private readonly CycleBalanceService _cycleBalanceService;
    private readonly RecurringOccurrenceService _occurrenceService;
    private readonly Stability.StabilityRecoveryService _stabilityRecoveryService;
    private readonly Stability.StabilityPlanRevisionService _stabilityPlanRevisionService;
    private readonly RecurringOccurrenceLedgerService _occurrenceLedger;
    private readonly FinancialClock _clock;
    private readonly SavingsGoals.ISharedPoolMutationLock _sharedPoolMutationLock;
    private readonly IRecurringPaymentMutationLock _recurringPaymentMutationLock;
    private readonly WishlistService _wishlistService;

    public TransactionPersistenceService(
        AppDbContext context,
        CycleBalanceService cycleBalanceService,
        RecurringOccurrenceService occurrenceService,
        Stability.StabilityRecoveryService stabilityRecoveryService,
        RecurringOccurrenceLedgerService? occurrenceLedger = null,
        FinancialClock? clock = null,
        Stability.StabilityPlanRevisionService? stabilityPlanRevisionService = null,
        SavingsGoals.ISharedPoolMutationLock? sharedPoolMutationLock = null,
        IRecurringPaymentMutationLock? recurringPaymentMutationLock = null,
        WishlistService? wishlistService = null)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService;
        _occurrenceService = occurrenceService;
        _stabilityRecoveryService = stabilityRecoveryService;
        _stabilityPlanRevisionService = stabilityPlanRevisionService
            ?? new Stability.StabilityPlanRevisionService(context);
        _clock = clock ?? FinancialClock.Utc;
        _occurrenceLedger = occurrenceLedger ?? new RecurringOccurrenceLedgerService(context, occurrenceService, _clock);
        _sharedPoolMutationLock = sharedPoolMutationLock ?? new SavingsGoals.SharedPoolMutationLock(context);
        _recurringPaymentMutationLock = recurringPaymentMutationLock ?? new RecurringPaymentMutationLock(context);
        _wishlistService = wishlistService ?? new WishlistService(
            context,
            cycleBalanceService,
            new SavingsGoals.SavingsGoalService(
                context,
                cycleBalanceService,
                _clock,
                occurrenceService,
                _occurrenceLedger,
                _sharedPoolMutationLock),
            _clock,
            _sharedPoolMutationLock);
    }

    public async Task<TransactionMutationResult> CreateTransactionAsync(
        TransactionMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var recurringLease = !string.IsNullOrWhiteSpace(request.RecurringPaymentId)
            ? await _recurringPaymentMutationLock.AcquireAsync(request.RecurringPaymentId, cancellationToken)
            : NoOpPoolLock.Instance;

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
            WishlistItemId = request.WishlistItemId,
            AccountId = NormalizeOptionalId(request.AccountId),
            CounterAccountId = NormalizeOptionalId(request.CounterAccountId),
        };
        if (transaction.WishlistItemId.HasValue)
        {
            return await RestoreWishlistPurchaseAsync(request, transaction, cancellationToken);
        }
        if (IsIncomeLedgerCategory(transaction.LedgerCategory))
        {
            transaction.AccountId = null;
            transaction.CounterAccountId = null;
        }
        transaction.ExcludeFromAutocomplete = TransactionAutocompletePolicy.ShouldExclude(transaction);
        // A real expense is still recordable when it crosses an earmark, but serialize it with
        // commitment funding/completion. Completion then either happens before this spend or sees
        // the resulting coverage shortfall; the two operations cannot both consume the same money.
        await using var poolLock = ReducesCommitmentBacking(transaction)
            ? await _sharedPoolMutationLock.AcquireAsync(cancellationToken)
            : NoOpPoolLock.Instance;
        var accountValidation = await ValidateAccountReferencesAsync(transaction, cancellationToken);
        if (accountValidation is not null) return accountValidation;
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
                // A date that is not an occurrence of this schedule is a bad request, not a fault.
                return new TransactionMutationResult(
                    TransactionMutationStatus.InvalidRecurringOccurrence,
                    Message: exception.Message);
            }
            var existingTxs = await _context.Transactions.Where(t =>
                t.RecurringPaymentId == payment.Id && t.RecurringOccurrenceDate == occurrence.Date.Value)
                .ToListAsync(cancellationToken);
            var nonDiscarded = existingTxs
                .Where(t => !RecurringOccurrenceAmounts.IsDiscardedMarker(t))
                .ToList();
            var paidSoFar = RecurringOccurrenceAmounts.PaidSoFar(existingTxs);
            var scheduled = Math.Abs(occurrenceRow.ScheduledAmount ?? payment.Amount);
            var remaining = RecurringOccurrenceAmounts.Remaining(scheduled, paidSoFar);

            if (occurrenceRow.Status is RecurringOccurrenceStatus.Discarded or RecurringOccurrenceStatus.SettledByLoanPayoff)
            {
                return new TransactionMutationResult(
                    TransactionMutationStatus.Conflict,
                    Message: "This recurring occurrence has already been settled.");
            }

            if (remaining <= 0m && nonDiscarded.Count > 0)
            {
                return new TransactionMutationResult(
                    TransactionMutationStatus.Conflict,
                    Message: "This recurring occurrence has already been settled.");
            }

            var isLinkedToLoan = await _context.Loans.AsNoTracking().AnyAsync(l => l.RecurringPaymentId == payment.Id, cancellationToken);
            if (!isLinkedToLoan && Math.Abs(transaction.Amount) > remaining)
            {
                return new TransactionMutationResult(
                    TransactionMutationStatus.Conflict,
                    Message: $"Payment amount cannot exceed the remaining amount of {remaining:N2}.");
            }
        }

        var splitContext = await LoadIncomeSplitContextAsync(
            transaction.LedgerCategory, TransactionDate.ToDateOnly(transaction.Date), transaction.PostedAt, transaction.Id,
            preserveHistoricalRecovery: false, cancellationToken: cancellationToken);
        var splitSpec = ResolveIncomeSplitSpec(transaction, splitContext);
        var splitAccountValidation = await ValidateIncomeSplitAccountIdsAsync(
            transaction.LedgerCategory,
            request.SplitAccountIds,
            cancellationToken);
        if (splitAccountValidation is not null) return splitAccountValidation;

        // Read before anything is staged, for the same reason LoadIncomeSplitContextAsync is.
        var documentsToRelink = await _context.VaultDocuments
            .Where(document => document.DetachedFromTransactionId == transaction.Id
                && document.TransactionId == null)
            .ToListAsync(cancellationToken);

        _context.Transactions.Add(transaction);
        if (occurrenceRow != null)
        {
            var existingTxs = await _context.Transactions.Where(t =>
                t.RecurringPaymentId == transaction.RecurringPaymentId && t.RecurringOccurrenceDate == transaction.RecurringOccurrenceDate)
                .ToListAsync(cancellationToken);
            var activeTxs = existingTxs
                .Where(t => !string.Equals(t.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var updatedPaid = activeTxs.Sum(t => Math.Abs(t.Amount)) + Math.Abs(transaction.Amount);
            RecurringOccurrenceLedgerService.SettleFromTransaction(occurrenceRow, transaction, updatedPaid);
        }
        foreach (var document in documentsToRelink)
        {
            document.TransactionId = transaction.Id;
            document.DetachedFromTransactionId = null;
        }
        await AddIncomeSplitTransactionsAsync(transaction, splitSpec, request.SplitAccountIds, cancellationToken);
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

        await using var recurringLease = !string.IsNullOrWhiteSpace(transaction.RecurringPaymentId)
            ? await _recurringPaymentMutationLock.AcquireAsync(transaction.RecurringPaymentId, cancellationToken)
            : NoOpPoolLock.Instance;

        if (await IsLoanRepaymentActionTransactionAsync(transaction.Id, cancellationToken))
        {
            return new TransactionMutationResult(
                TransactionMutationStatus.Conflict,
                transaction,
                "A loan repayment entry cannot be edited. Use Undo repayment from the loan instead.");
        }

        if (transaction.WishlistItemId.HasValue)
        {
            return new TransactionMutationResult(
                TransactionMutationStatus.Conflict,
                transaction,
                "A reward claim cannot be edited. Delete it to restore the reward, then claim it again.");
        }

        if (request.WishlistItemId.HasValue)
        {
            return new TransactionMutationResult(
                TransactionMutationStatus.Conflict,
                transaction,
                "A reward can only be claimed from the Rewards plan.");
        }

        await using var completionPoolLock = transaction.SavingsGoalId.HasValue
            ? await _sharedPoolMutationLock.AcquireAsync(cancellationToken)
            : NoOpPoolLock.Instance;

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
        if (request.RecurringPaymentId != null
            && !string.Equals(request.RecurringPaymentId, transaction.RecurringPaymentId, StringComparison.Ordinal))
        {
            return new TransactionMutationResult(
                TransactionMutationStatus.Conflict,
                transaction,
                "A transaction's recurring bill link cannot be changed after it is recorded.");
        }
        if (!string.IsNullOrWhiteSpace(request.RecurringOccurrenceDate))
        {
            if (!DateOnly.TryParseExact(request.RecurringOccurrenceDate, "yyyy-MM-dd", out var requestedOccurrence)
                || requestedOccurrence != transaction.RecurringOccurrenceDate)
            {
                return new TransactionMutationResult(
                    TransactionMutationStatus.Conflict,
                    transaction,
                    "A transaction's recurring occurrence date cannot be changed after it is recorded.");
            }
        }
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
            && originalRecoveryTopUp == nextRecoveryTopUp
            && originalAmount == amount
            && originalTransactionCycle != currentCycle;

        // Validate account identity before any context-tracked transaction or split rows can be
        // changed. The account lookup is deliberately separate from the existing transaction so
        // an invalid update cannot leave a dirty entity behind if the split resolver reads cached
        // cycle state and saves while it is loading.
        var accountProbe = new Transaction
        {
            Category = ledgerValidation.Category,
            LedgerCategory = ledgerValidation.LedgerCategory,
            Amount = amount,
            AccountId = ResolveUpdatedAccountId(
                request.AccountId,
                transaction.AccountId,
                transaction.LedgerCategory,
                ledgerValidation.LedgerCategory,
                counter: false),
            CounterAccountId = ResolveUpdatedAccountId(
                request.CounterAccountId,
                transaction.CounterAccountId,
                transaction.LedgerCategory,
                ledgerValidation.LedgerCategory,
                counter: true),
        };
        if (IsIncomeLedgerCategory(accountProbe.LedgerCategory))
        {
            accountProbe.AccountId = null;
            accountProbe.CounterAccountId = null;
        }
        await using var backingPoolLock = ReducesCommitmentBacking(transaction, accountProbe)
            ? await _sharedPoolMutationLock.AcquireAsync(cancellationToken)
            : NoOpPoolLock.Instance;
        var accountValidation = await ValidateAccountReferencesAsync(accountProbe, cancellationToken);
        if (accountValidation is not null) return accountValidation;

        var splitContext = await LoadIncomeSplitContextAsync(
            ledgerValidation.LedgerCategory, putDate, transaction.PostedAt, transaction.Id,
            preserveHistoricalRecovery, cancellationToken: cancellationToken);
        var splitAccountValidation = await ValidateIncomeSplitAccountIdsAsync(
            ledgerValidation.LedgerCategory,
            request.SplitAccountIds,
            cancellationToken);
        if (splitAccountValidation is not null) return splitAccountValidation;

        var existingSplits = await _context.Transactions
            .Where(t => t.Id.StartsWith(transaction.Id + "-split-"))
            .ToListAsync(cancellationToken);
        var preserveHistoricalIncomeSplit = settingForCycle != null
            && string.Equals(ledgerValidation.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase)
            && originalAmount == amount
            && TransactionDate.ToDateOnly(originalDate) == putDate
            && originalTransactionCycle != currentCycle
            && originalRecoveryTopUp.GetValueOrDefault() == nextRecoveryTopUp.GetValueOrDefault();

        transaction.Date = TransactionDate.PreserveTimeWhenSameDate(transaction.Date, putDate);
        transaction.Description = request.Description;
        transaction.Category = ledgerValidation.Category;
        transaction.LedgerCategory = ledgerValidation.LedgerCategory;
        transaction.Amount = amount;
        transaction.StabilityRecoveryTopUpAmount = nextRecoveryTopUp;
        transaction.StabilityReloadIntent = request.StabilityReloadIntent == null
            ? originalReloadIntent
            : StabilityReloadIntent.Normalize(request.StabilityReloadIntent);
        transaction.AccountId = accountProbe.AccountId;
        transaction.CounterAccountId = accountProbe.CounterAccountId;
        transaction.ExcludeFromAutocomplete = TransactionAutocompletePolicy.ShouldExclude(transaction);

        if (transaction.RecurringOccurrenceDate.HasValue)
        {
            if (putDate > _clock.Today) return InvalidDate("A recurring payment date cannot be in the future.");
            var occurrenceRow = await _occurrenceLedger.FindByTransactionAsync(transaction, cancellationToken);
            if (occurrenceRow != null)
            {
                var relatedTxs = await _context.Transactions
                    .Where(t => t.RecurringPaymentId == transaction.RecurringPaymentId
                        && t.RecurringOccurrenceDate == transaction.RecurringOccurrenceDate.Value
                        && t.Id != transaction.Id)
                    .ToListAsync(cancellationToken);
                relatedTxs.Add(transaction);
                RecurringOccurrenceLedgerService.RecomputeOccurrenceStatus(occurrenceRow, relatedTxs);
            }
        }

        if (preserveHistoricalIncomeSplit && Stability.HistoricalIncomeSplitRows.CanPreserve(transaction, existingSplits))
        {
            transaction.StabilityRecoveryTopUpAmount = nextRecoveryTopUp ?? 0m;
            Stability.HistoricalIncomeSplitRows.RefreshMetadata(transaction, existingSplits);
        }
        else
        {
            _context.Transactions.RemoveRange(existingSplits);
            var splitSpec = ResolveIncomeSplitSpec(transaction, splitContext);
            await AddIncomeSplitTransactionsAsync(transaction, splitSpec, request.SplitAccountIds, cancellationToken);
        }
        await ApplyWishlistPurchaseLinkAsync(transaction, cancellationToken);

        await SaveAndInvalidateCycleBalancesAsync(
            originalDate < transaction.Date ? originalDate : transaction.Date,
            cancellationToken);
        return new TransactionMutationResult(TransactionMutationStatus.Updated, transaction);
    }

}
