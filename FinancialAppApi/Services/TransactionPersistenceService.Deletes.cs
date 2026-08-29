using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public partial class TransactionPersistenceService
{
    public async Task<TransactionMutationResult> DeleteTransactionAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var transaction = await _context.Transactions.FindAsync([id], cancellationToken);
        if (transaction == null)
        {
            var reversedCompletion = await _context.SavingsGoalCompletions
                .AsNoTracking()
                .FirstOrDefaultAsync(completion => completion.TransactionId == id && completion.ReversedAt != null, cancellationToken);
            if (reversedCompletion != null)
            {
                return new TransactionMutationResult(TransactionMutationStatus.Deleted);
            }
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
                "A loan repayment entry cannot be deleted. Use Undo repayment from the loan instead.");
        }

        await using var poolLock = transaction.SavingsGoalId.HasValue
            || RemovingReducesCommitmentBacking(transaction)
            ? await _sharedPoolMutationLock.AcquireAsync(cancellationToken)
            : NoOpPoolLock.Instance;

        var goalRestoreError = await RestoreSavingsGoalCompletionAsync(transaction, cancellationToken);
        if (goalRestoreError != null)
        {
            return new TransactionMutationResult(TransactionMutationStatus.Conflict, transaction, goalRestoreError);
        }

        await ClearWishlistPurchaseLinkAsync(transaction, cancellationToken);

        var recurringOccurrence = await _occurrenceLedger.FindByTransactionAsync(transaction, cancellationToken);
        if (recurringOccurrence != null && transaction.RecurringOccurrenceDate.HasValue)
        {
            var remainingTxs = await _context.Transactions
                .Where(t => t.RecurringPaymentId == transaction.RecurringPaymentId
                    && t.RecurringOccurrenceDate == transaction.RecurringOccurrenceDate.Value
                    && t.Id != transaction.Id)
                .ToListAsync(cancellationToken);
            RecurringOccurrenceLedgerService.RecomputeOccurrenceStatus(recurringOccurrence, remainingTxs);
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
        CancellationToken cancellationToken = default,
        bool allowLoanRepaymentActionTransactions = false)
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
        await using var recurringLeases = await AcquireRecurringLocksAsync(
            transactions.Select(transaction => transaction.RecurringPaymentId),
            cancellationToken);
        await using var poolLock = transactions.Any(RemovingReducesCommitmentBacking)
            ? await _sharedPoolMutationLock.AcquireAsync(cancellationToken)
            : NoOpPoolLock.Instance;
        if (!allowLoanRepaymentActionTransactions)
        {
            var protectedIds = await LoanRepaymentActionTransactionIdsAsync(cancellationToken);
            if (transactions.Any(transaction => protectedIds.Contains(transaction.Id)))
            {
                return (
                    TransactionMutationStatus.Conflict,
                    [],
                    "Loan repayment entries cannot be deleted from the ledger. Use Undo repayment from the loan instead.");
            }
        }
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
            if (recurringOccurrence != null && transaction.RecurringOccurrenceDate.HasValue)
            {
                var remainingTxs = await _context.Transactions
                    .Where(t => t.RecurringPaymentId == transaction.RecurringPaymentId
                        && t.RecurringOccurrenceDate == transaction.RecurringOccurrenceDate.Value
                        && !canonicalIds.Contains(t.Id))
                    .ToListAsync(cancellationToken);
                RecurringOccurrenceLedgerService.RecomputeOccurrenceStatus(recurringOccurrence, remainingTxs);
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
        && string.Equals(left.AccountId, right.AccountId, StringComparison.Ordinal)
        && string.Equals(left.CounterAccountId, right.CounterAccountId, StringComparison.Ordinal)
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
            && string.Equals(existing.AccountId, NormalizeOptionalId(request.AccountId), StringComparison.Ordinal)
            && string.Equals(existing.CounterAccountId, NormalizeOptionalId(request.CounterAccountId), StringComparison.Ordinal)
            && string.Equals(
                existing.StabilityReloadIntent,
                StabilityReloadIntent.Normalize(request.StabilityReloadIntent),
                StringComparison.Ordinal);
    }
}
