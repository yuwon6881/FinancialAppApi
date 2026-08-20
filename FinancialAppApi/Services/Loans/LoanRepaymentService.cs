using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Loans;

public enum LoanRepaymentStatus
{
    Success,
    NotFound,
    Invalid,
    Conflict
}

public sealed record LoanRepaymentPreviewResult(
    LoanRepaymentStatus Status,
    int CyclesCount = 0,
    decimal TotalAmount = 0m,
    IReadOnlyList<LoanScheduleEntry>? Occurrences = null,
    string? Message = null);

public sealed record LoanRepaymentActionResult(
    LoanRepaymentStatus Status,
    string? ActionId = null,
    string? Kind = null,
    LoanView? LoanView = null,
    IReadOnlyList<Transaction>? Transactions = null,
    string? Message = null,
    string? Code = null,
    IReadOnlyList<string>? MissingBuckets = null);

public sealed class LoanRepaymentService
{
    private const int MaxAdvanceCycles = 60;
    private readonly AppDbContext _context;
    private readonly LoanService _loanService;
    private readonly TransactionPersistenceService _transactions;
    private readonly FinancialClock _clock;
    private readonly IRecurringPaymentMutationLock _paymentLock;

    public LoanRepaymentService(
        AppDbContext context,
        LoanService loanService,
        TransactionPersistenceService transactions,
        FinancialClock clock,
        IRecurringPaymentMutationLock? paymentLock = null)
    {
        _context = context;
        _loanService = loanService;
        _transactions = transactions;
        _clock = clock;
        _paymentLock = paymentLock ?? new RecurringPaymentMutationLock(context);
    }

    // Every repayment writes several ledger transactions plus schedule and occurrence changes that
    // are only meaningful together: a half-applied payoff leaves a closed bill with a live balance,
    // and transactions with no action row can never be undone. Npgsql is configured with
    // EnableRetryOnFailure, so a user-initiated transaction has to be opened inside the execution
    // strategy or it throws. Deliberately does NOT clear the change tracker between attempts:
    // RecurringOccurrenceLedgerService caches the occurrence entities it materialises, and detaching
    // them leaves it mutating objects EF has stopped tracking, so the status changes silently vanish.
    private async Task<LoanRepaymentActionResult> InTransactionAsync(
        Func<Task<LoanRepaymentActionResult>> work,
        CancellationToken cancellationToken)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var dbTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            var result = await work();
            if (result.Status != LoanRepaymentStatus.Success)
            {
                // A refusal must leave nothing behind: earlier cycles of an advance repayment have
                // already been saved inside this transaction by the time a later one is rejected.
                await dbTransaction.RollbackAsync(cancellationToken);
                return result;
            }
            await _context.SaveChangesAsync(cancellationToken);
            await dbTransaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    public async Task<LoanRepaymentPreviewResult> PreviewAdvanceRepaymentAsync(
        string loanId,
        int cycles,
        CancellationToken cancellationToken = default)
    {
        if (cycles is < 1 or > MaxAdvanceCycles)
        {
            return new LoanRepaymentPreviewResult(LoanRepaymentStatus.Invalid, Message: $"Repayment must include between 1 and {MaxAdvanceCycles} cycles.");
        }

        var view = await _loanService.GetLoanAsync(loanId, cancellationToken);
        if (view == null) return new LoanRepaymentPreviewResult(LoanRepaymentStatus.NotFound);

        var paymentMode = await _context.RecurringPayments.AsNoTracking()
            .Where(payment => payment.Id == view.Loan.RecurringPaymentId)
            .Select(payment => payment.PaymentMode)
            .FirstOrDefaultAsync(cancellationToken);
        if (paymentMode == RecurringPaymentMode.AutoDeduct)
        {
            return new LoanRepaymentPreviewResult(
                LoanRepaymentStatus.Invalid,
                Message: "An automatically deducted bill cannot be settled in advance.");
        }

        if (view.Loan.ScheduleStatus == LoanScheduleStatus.Incomplete)
        {
            return new LoanRepaymentPreviewResult(LoanRepaymentStatus.Invalid, Message: "Loan schedule is incomplete or unavailable.");
        }

        var available = view.Replay.FutureSchedule;
        if (available.Count == 0 || view.Replay.OutstandingBalance <= 0m)
        {
            return new LoanRepaymentPreviewResult(LoanRepaymentStatus.Invalid, Message: "This loan has already been fully paid.");
        }

        var count = Math.Min(cycles, available.Count);
        var entries = available.Take(count).ToList();
        var totalAmount = entries.Sum(e => e.Payment);

        return new LoanRepaymentPreviewResult(
            LoanRepaymentStatus.Success,
            CyclesCount: count,
            TotalAmount: totalAmount,
            Occurrences: entries);
    }

    public async Task<LoanRepaymentActionResult> AdvanceCyclesRepaymentAsync(
        string loanId,
        int cycles,
        string? accountId,
        string? clientKey,
        DateTime? postedAt = null,
        CancellationToken cancellationToken = default)
    {
        if (cycles is < 1 or > MaxAdvanceCycles)
        {
            return new LoanRepaymentActionResult(LoanRepaymentStatus.Invalid, Message: $"Repayment must include between 1 and {MaxAdvanceCycles} cycles.");
        }

        await using var paymentLease = await AcquireLoanPaymentLockAsync(loanId, cancellationToken);

        var outcome = await InTransactionAsync(
            () => AdvanceCyclesCoreAsync(loanId, cycles, accountId, clientKey, postedAt, cancellationToken),
            cancellationToken);
        if (outcome.Status != LoanRepaymentStatus.Success) return outcome;
        return outcome with { LoanView = await _loanService.GetLoanAsync(loanId, cancellationToken) };
    }

    private async Task<LoanRepaymentActionResult> AdvanceCyclesCoreAsync(
        string loanId,
        int cycles,
        string? accountId,
        string? clientKey,
        DateTime? postedAt,
        CancellationToken cancellationToken)
    {
        var loan = await _context.Loans.FirstOrDefaultAsync(l => l.Id == loanId, cancellationToken);
        if (loan == null) return new LoanRepaymentActionResult(LoanRepaymentStatus.NotFound);

        var payment = await _context.RecurringPayments.FirstOrDefaultAsync(p => p.Id == loan.RecurringPaymentId, cancellationToken);
        if (payment == null) return new LoanRepaymentActionResult(LoanRepaymentStatus.NotFound);

        var replay = await ReplayExistingActionAsync(loanId, clientKey, LoanRepaymentActionKind.AdvanceCycles, cancellationToken);
        if (replay != null) return replay;

        if (payment.PaymentMode == RecurringPaymentMode.AutoDeduct)
        {
            return new LoanRepaymentActionResult(
                LoanRepaymentStatus.Conflict,
                Message: "An automatically deducted bill cannot be settled in advance.");
        }

        var view = await _loanService.GetLoanAsync(loanId, cancellationToken);
        if (view == null || view.Loan.ScheduleStatus == LoanScheduleStatus.Incomplete)
        {
            return new LoanRepaymentActionResult(LoanRepaymentStatus.Invalid, Message: "Loan schedule is incomplete or unavailable.");
        }

        var futureSchedule = view.Replay.FutureSchedule;
        if (futureSchedule.Count == 0 || view.Replay.OutstandingBalance <= 0m)
        {
            return new LoanRepaymentActionResult(LoanRepaymentStatus.Invalid, Message: "This loan has already been fully paid.");
        }

        var targetCount = Math.Min(cycles, futureSchedule.Count);
        var selectedEntries = futureSchedule.Take(targetCount).ToList();

        // A loan repayment is a one-off financing decision, not a change to the linked bill's
        // default account. It may come from any open account; the generated ledger row follows
        // that chosen account's bucket while its occurrence tags continue to drive loan replay.
        var requestedAccountId = string.IsNullOrWhiteSpace(accountId) ? payment.AccountId : accountId.Trim();
        if (string.IsNullOrWhiteSpace(requestedAccountId))
        {
            return new LoanRepaymentActionResult(
                LoanRepaymentStatus.Invalid,
                Message: "Choose the account for this loan repayment.",
                Code: "ledger_account_required");
        }

        var account = await _context.LedgerAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == requestedAccountId, cancellationToken);
        if (account is null || account.IsArchived)
        {
            return new LoanRepaymentActionResult(
                LoanRepaymentStatus.Invalid,
                Message: "Choose an open account for this loan repayment.",
                Code: "ledger_account_invalid");
        }

        var createdTransactions = new List<Transaction>();
        var targetOccurrenceDates = new List<string>();
        var actionId = BuildRepaymentActionId(clientKey);

        for (var i = 0; i < selectedEntries.Count; i++)
        {
            var entry = selectedEntries[i];
            var dateStr = entry.OccurrenceDate.ToString("yyyy-MM-dd");
            targetOccurrenceDates.Add(dateStr);

            var txId = string.IsNullOrWhiteSpace(clientKey)
                ? BuildRepaymentTransactionId($"{loanId}:advance:{dateStr}:{i}")
                : $"{clientKey}-c{i + 1}";

            var mutation = await _transactions.CreateTransactionAsync(new TransactionMutationRequest(
                txId,
                _clock.Today.ToString("yyyy-MM-dd"),
                postedAt?.ToUniversalTime().ToString("O"),
                $"{loan.Name} advance repayment",
                payment.Category,
                account.Bucket,
                ObfuscationHelper.Obfuscate(-Math.Abs(entry.Payment)),
                payment.Id,
                null,
                dateStr,
                AccountId: requestedAccountId), cancellationToken);

            if (mutation.Status is not (TransactionMutationStatus.Created or TransactionMutationStatus.Existing))
            {
                return new LoanRepaymentActionResult(
                    mutation.Status == TransactionMutationStatus.Conflict ? LoanRepaymentStatus.Conflict : LoanRepaymentStatus.Invalid,
                    Message: mutation.Message ?? "Failed to create repayment transaction.");
            }

            if (mutation.Transaction != null)
            {
                createdTransactions.Add(mutation.Transaction);
            }
        }

        var action = new LoanRepaymentAction
        {
            Id = actionId,
            UserId = loan.UserId,
            LoanId = loan.Id,
            RecurringPaymentId = loan.RecurringPaymentId,
            Kind = LoanRepaymentActionKind.AdvanceCycles,
            EffectiveDate = _clock.Today,
            ReplayRevision = Guid.NewGuid().ToString("N"),
            PriorPaymentActive = payment.Active,
            PriorPaymentEndDate = payment.EndDate,
            TransactionIds = string.Join(",", createdTransactions.Select(t => t.Id)),
            TargetOccurrenceDates = string.Join(",", targetOccurrenceDates),
            UndoFingerprints = JsonSerializer.Serialize(createdTransactions.Select(t => new { t.Id, t.Amount, t.Date })),
            CreatedAt = DateTime.UtcNow
        };

        _context.LoanRepaymentActions.Add(action);

        return new LoanRepaymentActionResult(
            LoanRepaymentStatus.Success,
            ActionId: actionId,
            Kind: LoanRepaymentActionKind.AdvanceCycles,
            Transactions: createdTransactions);
    }

    public async Task<LoanRepaymentActionResult> FullSettlementRepaymentAsync(
        string loanId,
        decimal lenderQuoteAmount,
        string? accountId,
        string? clientKey,
        DateTime? postedAt = null,
        CancellationToken cancellationToken = default)
    {
        if (lenderQuoteAmount < 0.01m)
        {
            return new LoanRepaymentActionResult(LoanRepaymentStatus.Invalid, Message: "Settlement amount must be at least 0.01.");
        }

        await using var paymentLease = await AcquireLoanPaymentLockAsync(loanId, cancellationToken);

        var outcome = await InTransactionAsync(
            () => FullSettlementCoreAsync(loanId, lenderQuoteAmount, accountId, clientKey, postedAt, cancellationToken),
            cancellationToken);
        if (outcome.Status != LoanRepaymentStatus.Success) return outcome;
        return outcome with { LoanView = await _loanService.GetLoanAsync(loanId, cancellationToken) };
    }

    private async Task<LoanRepaymentActionResult> FullSettlementCoreAsync(
        string loanId,
        decimal lenderQuoteAmount,
        string? accountId,
        string? clientKey,
        DateTime? postedAt,
        CancellationToken cancellationToken)
    {
        var loan = await _context.Loans.FirstOrDefaultAsync(l => l.Id == loanId, cancellationToken);
        if (loan == null) return new LoanRepaymentActionResult(LoanRepaymentStatus.NotFound);

        var payment = await _context.RecurringPayments.FirstOrDefaultAsync(p => p.Id == loan.RecurringPaymentId, cancellationToken);
        if (payment == null) return new LoanRepaymentActionResult(LoanRepaymentStatus.NotFound);

        var replay = await ReplayExistingActionAsync(loanId, clientKey, LoanRepaymentActionKind.FullSettlement, cancellationToken);
        if (replay != null) return replay;

        // The advance path refuses both of these, and a payoff writes far more state, so it must not
        // be the lenient one. An incomplete schedule means the replay cannot say what is really owed.
        if (payment.PaymentMode == RecurringPaymentMode.AutoDeduct)
        {
            return new LoanRepaymentActionResult(
                LoanRepaymentStatus.Conflict,
                Message: "An automatically deducted bill cannot be settled early. Switch it to manual payment first.");
        }

        // One payoff per loan, asked before the balance is read: a settled loan's replay is overridden
        // to a zero balance, so checking that first answered a second settlement with "already fully
        // paid", which names the symptom rather than the thing standing in the way.
        var alreadySettled = await _context.LoanRepaymentActions
            .AnyAsync(a => a.LoanId == loan.Id && a.Kind == LoanRepaymentActionKind.FullSettlement, cancellationToken);
        if (alreadySettled)
        {
            return new LoanRepaymentActionResult(
                LoanRepaymentStatus.Conflict,
                Message: "This loan has already been settled. Undo the existing settlement first.");
        }

        var view = await _loanService.GetLoanAsync(loanId, cancellationToken);
        if (view == null || view.Loan.ScheduleStatus == LoanScheduleStatus.Incomplete)
        {
            return new LoanRepaymentActionResult(LoanRepaymentStatus.Invalid, Message: "Loan schedule is incomplete or unavailable.");
        }
        if (view.Replay.OutstandingBalance <= 0m)
        {
            return new LoanRepaymentActionResult(LoanRepaymentStatus.Invalid, Message: "This loan has already been fully paid.");
        }

        // Payoff closes the bill as of today, so anything still open from before today would be
        // stranded: invisible once the bill goes inactive, yet still counted as committed money and
        // still blocking deletion. Those are real unpaid instalments, so the user resolves them.
        var arrears = await _context.RecurringPaymentOccurrences
            .Where(o => o.RecurringPaymentId == payment.Id
                && o.OccurrenceDate < _clock.Today
                && (o.Status == RecurringOccurrenceStatus.Pending || o.Status == RecurringOccurrenceStatus.PartiallyPaid))
            .OrderBy(o => o.OccurrenceDate)
            .Select(o => o.OccurrenceDate)
            .ToListAsync(cancellationToken);
        if (arrears.Count > 0)
        {
            var dates = string.Join(", ", arrears.Select(d => d.ToString("yyyy-MM-dd")));
            return new LoanRepaymentActionResult(
                LoanRepaymentStatus.Conflict,
                Message: $"Record or discard the payments still open from before today first: {dates}.");
        }

        var requestedAccountId = string.IsNullOrWhiteSpace(accountId) ? payment.AccountId : accountId.Trim();
        if (string.IsNullOrWhiteSpace(requestedAccountId))
        {
            return new LoanRepaymentActionResult(
                LoanRepaymentStatus.Invalid,
                Message: "Choose the account for this loan full settlement.",
                Code: "ledger_account_required");
        }

        var account = await _context.LedgerAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == requestedAccountId, cancellationToken);
        if (account is null || account.IsArchived)
        {
            return new LoanRepaymentActionResult(
                LoanRepaymentStatus.Invalid,
                Message: "Choose an open account for this loan settlement.",
                Code: "ledger_account_invalid");
        }

        var nextOccurrence = view.Replay.FutureSchedule.FirstOrDefault()?.OccurrenceDate ?? _clock.Today;
        var dateStr = nextOccurrence.ToString("yyyy-MM-dd");
        var txId = string.IsNullOrWhiteSpace(clientKey)
            ? BuildRepaymentTransactionId($"{loanId}:settlement:{dateStr}")
            : clientKey;

        var mutation = await _transactions.CreateTransactionAsync(new TransactionMutationRequest(
            txId,
            _clock.Today.ToString("yyyy-MM-dd"),
            postedAt?.ToUniversalTime().ToString("O"),
            $"{loan.Name} full settlement",
            payment.Category,
            account.Bucket,
            ObfuscationHelper.Obfuscate(-Math.Abs(lenderQuoteAmount)),
            payment.Id,
            null,
            dateStr,
            AccountId: requestedAccountId), cancellationToken);

        if (mutation.Status is not (TransactionMutationStatus.Created or TransactionMutationStatus.Existing))
        {
            return new LoanRepaymentActionResult(
                mutation.Status == TransactionMutationStatus.Conflict ? LoanRepaymentStatus.Conflict : LoanRepaymentStatus.Invalid,
                Message: mutation.Message ?? "Failed to create settlement transaction.");
        }

        var priorActive = payment.Active;
        var priorEndDate = payment.EndDate;

        // Stop the linked recurring bill as of today
        payment.Active = false;
        payment.EndDate = _clock.Today.ToString("yyyy-MM-dd");

        // Every still-open occurrence, with no date floor. The arrears guard above has already
        // established there is nothing before today, so this is the same set — but written without a
        // floor it cannot silently strand a row if that guard is ever relaxed.
        var occurrencesToResolve = await _context.RecurringPaymentOccurrences
            .Where(o => o.RecurringPaymentId == payment.Id
                && (o.Status == RecurringOccurrenceStatus.Pending
                    || o.Status == RecurringOccurrenceStatus.PartiallyPaid
                    || o.OccurrenceDate == nextOccurrence))
            .ToListAsync(cancellationToken);

        var settledDates = new List<string>(occurrencesToResolve.Count);
        foreach (var occ in occurrencesToResolve)
        {
            occ.Status = RecurringOccurrenceStatus.SettledByLoanPayoff;
            settledDates.Add(occ.OccurrenceDate.ToString("yyyy-MM-dd"));
        }

        var actionId = BuildRepaymentActionId(clientKey);
        var createdTx = mutation.Transaction!;
        var action = new LoanRepaymentAction
        {
            Id = actionId,
            UserId = loan.UserId,
            LoanId = loan.Id,
            RecurringPaymentId = loan.RecurringPaymentId,
            Kind = LoanRepaymentActionKind.FullSettlement,
            LenderQuoteAmount = lenderQuoteAmount,
            EffectiveDate = _clock.Today,
            ReplayRevision = Guid.NewGuid().ToString("N"),
            PriorPaymentActive = priorActive,
            PriorPaymentEndDate = priorEndDate,
            TransactionIds = createdTx.Id,
            // Every occurrence this payoff swallowed, so undo restores exactly those and leaves a row
            // settled by some other action alone.
            TargetOccurrenceDates = string.Join(",", settledDates),
            UndoFingerprints = JsonSerializer.Serialize(new[] { new { createdTx.Id, createdTx.Amount, createdTx.Date } }),
            CreatedAt = DateTime.UtcNow
        };

        _context.LoanRepaymentActions.Add(action);

        return new LoanRepaymentActionResult(
            LoanRepaymentStatus.Success,
            ActionId: actionId,
            Kind: LoanRepaymentActionKind.FullSettlement,
            Transactions: [createdTx]);
    }

    public async Task<LoanRepaymentActionResult> UndoRepaymentActionAsync(
        string actionId,
        CancellationToken cancellationToken = default)
    {
        var loanId = await _context.LoanRepaymentActions
            .Where(a => a.Id == actionId)
            .Select(a => a.LoanId)
            .FirstOrDefaultAsync(cancellationToken);
        if (loanId == null) return new LoanRepaymentActionResult(LoanRepaymentStatus.NotFound);

        var paymentId = await _context.LoanRepaymentActions
            .Where(action => action.Id == actionId)
            .Select(action => action.RecurringPaymentId)
            .FirstAsync(cancellationToken);
        await using var paymentLease = await _paymentLock.AcquireAsync(paymentId, cancellationToken);

        var outcome = await InTransactionAsync(
            () => UndoRepaymentActionCoreAsync(actionId, cancellationToken),
            cancellationToken);
        if (outcome.Status != LoanRepaymentStatus.Success) return outcome;
        return outcome with { LoanView = await _loanService.GetLoanAsync(loanId, cancellationToken) };
    }

    private async Task<LoanRepaymentActionResult> UndoRepaymentActionCoreAsync(
        string actionId,
        CancellationToken cancellationToken)
    {
        var action = await _context.LoanRepaymentActions
            .FirstOrDefaultAsync(a => a.Id == actionId, cancellationToken);
        if (action == null) return new LoanRepaymentActionResult(LoanRepaymentStatus.NotFound);

        var txIds = action.TransactionIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (action.Kind == LoanRepaymentActionKind.FullSettlement)
        {
            var payment = await _context.RecurringPayments.FirstOrDefaultAsync(p => p.Id == action.RecurringPaymentId, cancellationToken);
            if (payment != null)
            {
                payment.Active = action.PriorPaymentActive;
                payment.EndDate = action.PriorPaymentEndDate;
            }

            // Only the occurrences this payoff actually swallowed, and each restored to what its own
            // ledger rows say rather than a blanket Pending: a row that was PartiallyPaid when the
            // payoff took it still has its part-payment, and coming back as Pending would leave the
            // status contradicting the money sitting against it.
            var targetDates = action.TargetOccurrenceDates
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => DateOnly.TryParse(value, out var parsed) ? parsed : (DateOnly?)null)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToList();

            var occurrences = await _context.RecurringPaymentOccurrences
                .Where(o => o.RecurringPaymentId == action.RecurringPaymentId
                    && o.Status == RecurringOccurrenceStatus.SettledByLoanPayoff
                    && targetDates.Contains(o.OccurrenceDate))
                .ToListAsync(cancellationToken);

            if (occurrences.Count > 0)
            {
                var occurrenceDates = occurrences.Select(o => o.OccurrenceDate).ToList();
                var relatedTransactions = await _context.Transactions
                    .Where(t => t.RecurringPaymentId == action.RecurringPaymentId
                        && t.RecurringOccurrenceDate != null
                        && occurrenceDates.Contains(t.RecurringOccurrenceDate.Value)
                        && !txIds.Contains(t.Id))
                    .ToListAsync(cancellationToken);

                foreach (var occ in occurrences)
                {
                    // RecomputeOccurrenceStatus refuses to touch a SettledByLoanPayoff row, which is
                    // what protects it from ordinary transaction edits; this undo is the one caller
                    // entitled to reopen it, so it clears the marker first.
                    occ.Status = RecurringOccurrenceStatus.Pending;
                    occ.PaidDate = null;
                    RecurringOccurrenceLedgerService.RecomputeOccurrenceStatus(
                        occ,
                        relatedTransactions.Where(t => t.RecurringOccurrenceDate == occ.OccurrenceDate).ToList());
                }
            }
        }

        if (txIds.Length > 0)
        {
            // A rejected delete used to be discarded, so the action row vanished while its
            // transactions stayed on the ledger and could never be undone again.
            var deletion = await _transactions.DeleteTransactionsAsync(
                txIds,
                cancellationToken,
                allowLoanRepaymentActionTransactions: true);
            if (deletion.Status is not (TransactionMutationStatus.Deleted or TransactionMutationStatus.NotFound))
            {
                return new LoanRepaymentActionResult(
                    LoanRepaymentStatus.Conflict,
                    Message: deletion.Message ?? "The repayment transactions could not be removed.");
            }
        }

        _context.LoanRepaymentActions.Remove(action);

        return new LoanRepaymentActionResult(
            LoanRepaymentStatus.Success,
            ActionId: actionId,
            Kind: action.Kind);
    }

    private static string BuildRepaymentTransactionId(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"tx-loanrepay-{Convert.ToHexString(hash).ToLowerInvariant()[..16]}";
    }

    private async Task<IAsyncDisposable> AcquireLoanPaymentLockAsync(
        string loanId,
        CancellationToken cancellationToken)
    {
        var paymentId = await _context.Loans.AsNoTracking()
            .Where(loan => loan.Id == loanId)
            .Select(loan => loan.RecurringPaymentId)
            .FirstOrDefaultAsync(cancellationToken);
        return await _paymentLock.AcquireAsync(paymentId ?? string.Empty, cancellationToken);
    }

    private async Task<LoanRepaymentActionResult?> ReplayExistingActionAsync(
        string loanId,
        string? clientKey,
        string expectedKind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientKey)) return null;
        var actionId = BuildRepaymentActionId(clientKey);
        var action = await _context.LoanRepaymentActions.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == actionId, cancellationToken);
        if (action == null) return null;
        if (action.LoanId != loanId || action.Kind != expectedKind)
        {
            return new LoanRepaymentActionResult(
                LoanRepaymentStatus.Conflict,
                Message: "This repayment request key was already used for another action.");
        }

        var transactionIds = action.TransactionIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var transactions = await _context.Transactions.AsNoTracking()
            .Where(transaction => transactionIds.Contains(transaction.Id))
            .ToListAsync(cancellationToken);
        return new LoanRepaymentActionResult(
            LoanRepaymentStatus.Success,
            ActionId: action.Id,
            Kind: action.Kind,
            Transactions: transactions);
    }

    private static string BuildRepaymentActionId(string? clientKey)
    {
        if (string.IsNullOrWhiteSpace(clientKey)) return $"repay-{Guid.NewGuid():N}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"loan-repayment-action:{clientKey}"));
        return $"repay-{Convert.ToHexString(hash).ToLowerInvariant()[..24]}";
    }
}
