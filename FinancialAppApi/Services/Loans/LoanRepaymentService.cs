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

public sealed partial class LoanRepaymentService
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

}
