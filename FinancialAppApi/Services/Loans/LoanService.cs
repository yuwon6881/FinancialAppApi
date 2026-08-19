using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Loans;

public enum LoanMutationStatus
{
    Success,
    NotFound,
    IdMismatch,
    Invalid,
    RecurringPaymentNotFound,
    RecurringPaymentAlreadyLinked,
    Conflict
}

public sealed record LoanResult(
    LoanMutationStatus Status,
    LoanView? View = null,
    string? Message = null);

public sealed record LoanView(
    Loan Loan,
    RecurringPayment? RecurringPayment,
    LoanReplayResult Replay,
    // Set only while a full settlement stands, and it is the handle the client needs to undo it.
    string? SettlementActionId = null);

/// <summary>
/// Stores loan terms and builds the read model by replaying the linked bill's complete settlement
/// history. No balance is persisted: deleting or restoring a ledger row changes the replay only.
/// </summary>
public sealed class LoanService
{
    private readonly AppDbContext _context;
    private readonly RecurringOccurrenceLedgerService _occurrences;

    public LoanService(
        AppDbContext context,
        RecurringOccurrenceLedgerService? occurrences = null,
        FinancialClock? clock = null)
    {
        _context = context;
        var resolvedClock = clock ?? FinancialClock.Utc;
        _occurrences = occurrences ?? new RecurringOccurrenceLedgerService(
            context,
            new RecurringOccurrenceService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RecurringOccurrenceService>.Instance),
            resolvedClock);
    }

    public async Task<List<LoanView>> GetLoansAsync(CancellationToken cancellationToken = default)
    {
        var loans = await _context.Loans
            .AsNoTracking()
            .OrderBy(loan => loan.Name)
            .ThenBy(loan => loan.Id)
            .ToListAsync(cancellationToken);
        return await BuildViewsAsync(loans, previewScheduleLength: 6, cancellationToken);
    }

    public async Task<LoanView?> GetLoanAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var loan = await _context.Loans
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (loan == null) return null;
        return (await BuildViewsAsync([loan], previewScheduleLength: null, cancellationToken)).Single();
    }

    public async Task<LoanResult> CreateLoanAsync(
        Loan loan,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(loan.Id))
            return Invalid("A loan ID is required.");

        var existing = await _context.Loans
            .FirstOrDefaultAsync(candidate => candidate.Id == loan.Id, cancellationToken);
        if (existing != null)
        {
            var existingView = await GetLoanAsync(existing.Id, cancellationToken);
            return new LoanResult(LoanMutationStatus.Success, existingView);
        }

        var validation = Validate(loan, validateTerm: false);
        if (validation != null) return validation;

        var paymentStatus = await ValidateRecurringPaymentLinkAsync(
            loan.RecurringPaymentId,
            loan.Id,
            cancellationToken);
        if (paymentStatus != null) return paymentStatus;

        var payment = await _context.RecurringPayments
            .FirstAsync(candidate => candidate.Id == loan.RecurringPaymentId, cancellationToken);
        CaptureScheduleSnapshot(loan, payment);
        var termStatus = await SynchronizeNewLinkTermAsync(loan, payment, cancellationToken);
        if (termStatus != null) return termStatus;
        validation = Validate(loan);
        if (validation != null) return validation;

        _context.Loans.Add(loan);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return new LoanResult(
                LoanMutationStatus.Conflict,
                Message: "This loan could not be saved because its bill is already linked to another loan.");
        }

        return new LoanResult(
            LoanMutationStatus.Success,
            await GetLoanAsync(loan.Id, cancellationToken));
    }

    public async Task<LoanResult> UpdateLoanAsync(
        string id,
        Loan updated,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(id, updated.Id, StringComparison.Ordinal))
            return new LoanResult(LoanMutationStatus.IdMismatch, Message: "ID mismatch.");

        var loan = await _context.Loans
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (loan == null) return new LoanResult(LoanMutationStatus.NotFound);

        var linkChanged = !string.Equals(updated.RecurringPaymentId, loan.RecurringPaymentId, StringComparison.Ordinal);
        var termChanged = updated.TermPeriods != loan.TermPeriods;

        var validation = Validate(updated, validateTerm: !linkChanged);
        if (validation != null) return validation;

        var paymentStatus = await ValidateRecurringPaymentLinkAsync(
            updated.RecurringPaymentId,
            id,
            cancellationToken,
            allowMissingExistingLink: !linkChanged);
        if (paymentStatus != null) return paymentStatus;

        loan.Name = updated.Name.Trim();
        loan.OpeningPrincipal = updated.OpeningPrincipal;
        loan.TrackingStartDate = updated.TrackingStartDate;
        loan.AnnualRatePercent = updated.AnnualRatePercent;
        loan.RateBasis = updated.RateBasis;
        loan.TermPeriods = updated.TermPeriods;
        loan.InterestMethod = updated.InterestMethod;

        RecurringPayment? payment = null;
        if (linkChanged)
        {
            payment = await _context.RecurringPayments
                .FirstAsync(candidate => candidate.Id == updated.RecurringPaymentId, cancellationToken);
            loan.RecurringPaymentId = updated.RecurringPaymentId;
            CaptureScheduleSnapshot(loan, payment);
            var termStatus = await SynchronizeNewLinkTermAsync(loan, payment, cancellationToken);
            if (termStatus != null) return termStatus;
            validation = Validate(loan);
            if (validation != null) return validation;
        }
        else
        {
            payment = await _context.RecurringPayments
                .FirstOrDefaultAsync(candidate => candidate.Id == loan.RecurringPaymentId, cancellationToken);
            if (payment != null
                && (termChanged || string.IsNullOrWhiteSpace(payment.EndDate))
                && LoanTermSchedule.TryGetEndDate(loan, out var endDate))
            {
                var serializedEndDate = endDate.ToString("yyyy-MM-dd");
                if (!string.Equals(payment.EndDate, serializedEndDate, StringComparison.Ordinal))
                {
                    await _occurrences.PreserveThroughTodayAndResetFutureAsync(
                        payment,
                        cancellationToken: cancellationToken);
                    payment.EndDate = serializedEndDate;
                }
            }
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return new LoanResult(
                LoanMutationStatus.Conflict,
                Message: "This loan could not be saved because its bill is already linked to another loan.");
        }

        return new LoanResult(
            LoanMutationStatus.Success,
            await GetLoanAsync(id, cancellationToken));
    }

    public async Task<LoanMutationStatus> DeleteLoanAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var loan = await _context.Loans
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (loan == null) return LoanMutationStatus.NotFound;

        _context.Loans.Remove(loan);
        await _context.SaveChangesAsync(cancellationToken);
        return LoanMutationStatus.Success;
    }

    private async Task<List<LoanView>> BuildViewsAsync(
        IReadOnlyList<Loan> loans,
        int? previewScheduleLength,
        CancellationToken cancellationToken)
    {
        if (loans.Count == 0) return [];

        var paymentIds = loans
            .Select(loan => loan.RecurringPaymentId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var payments = await _context.RecurringPayments
            .AsNoTracking()
            .Where(payment => paymentIds.Contains(payment.Id))
            .ToDictionaryAsync(payment => payment.Id, cancellationToken);
        var earliestTrackingStart = loans.Min(loan => loan.TrackingStartDate);
        var transactions = await _context.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.RecurringPaymentId != null
                && paymentIds.Contains(transaction.RecurringPaymentId)
                && (transaction.RecurringOccurrenceDate == null
                    || transaction.RecurringOccurrenceDate >= earliestTrackingStart))
            .ToListAsync(cancellationToken);

        var transactionsByPayment = transactions
            .GroupBy(transaction => transaction.RecurringPaymentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var inactivePaymentLoanIds = loans
            .Where(loan => payments.TryGetValue(loan.RecurringPaymentId, out var payment) && !payment.Active)
            .Select(loan => loan.Id)
            .ToArray();

        var fullSettlementActions = new Dictionary<string, LoanRepaymentAction>(StringComparer.Ordinal);
        if (inactivePaymentLoanIds.Length > 0)
        {
            // Grouped rather than ToDictionaryAsync keyed on LoanId: a unique index now stops a
            // second settlement per loan, but a pre-existing duplicate must not throw here and take
            // the entire loan list — and global search with it — down for good.
            var settlementRows = await _context.LoanRepaymentActions
                .AsNoTracking()
                .Where(action => inactivePaymentLoanIds.Contains(action.LoanId) && action.Kind == LoanRepaymentActionKind.FullSettlement)
                .ToListAsync(cancellationToken);
            fullSettlementActions = settlementRows
                .GroupBy(action => action.LoanId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderBy(action => action.CreatedAt).First(), StringComparer.Ordinal);
        }

        return loans.Select(loan =>
        {
            var loanTransactions = transactionsByPayment.GetValueOrDefault(loan.RecurringPaymentId) ?? [];
            var inputs = loanTransactions
                .Where(transaction => transaction.RecurringOccurrenceDate != null)
                .Select(transaction => new LoanPaymentInput(
                    transaction.RecurringOccurrenceDate!.Value,
                    transaction.PostedAt,
                    transaction.Amount,
                    string.Equals(transaction.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase),
                    transaction.Id))
                .ToList();

            payments.TryGetValue(loan.RecurringPaymentId, out var payment);
            var hasLegacyHistory = loanTransactions.Any(transaction => transaction.RecurringOccurrenceDate == null);
            if (payment == null || hasLegacyHistory)
            {
                loan.ScheduleStatus = LoanScheduleStatus.Incomplete;
            }
            var replay = LoanReplay.Replay(loan, inputs);
            fullSettlementActions.TryGetValue(loan.Id, out var settlementAction);
            if (settlementAction != null)
            {
                replay = replay with
                {
                    OutstandingBalance = 0m,
                    PayoffDate = settlementAction.EffectiveDate,
                    FutureSchedule = []
                };
            }
            else if (previewScheduleLength.HasValue && replay.FutureSchedule.Count > previewScheduleLength.Value)
            {
                replay = replay with
                {
                    FutureSchedule = replay.FutureSchedule.Take(previewScheduleLength.Value).ToList()
                };
            }
            return new LoanView(loan, payment, replay, settlementAction?.Id);
        }).ToList();
    }

    private static void CaptureScheduleSnapshot(Loan loan, RecurringPayment payment)
    {
        loan.ScheduleFrequency = string.Equals(payment.Frequency, "Monthly", StringComparison.OrdinalIgnoreCase)
            ? "Monthly"
            : string.Equals(payment.Frequency, "Annually", StringComparison.OrdinalIgnoreCase)
                ? "Annually"
                : null;
        loan.ScheduleDueDay = payment.DueDate is >= 1 and <= 31
            ? payment.DueDate
            : null;
        loan.ScheduleStartDate = DateOnly.TryParseExact(payment.StartDate, "yyyy-MM-dd", out var start)
            ? start
            : null;
        loan.ScheduleStatus = loan.ScheduleFrequency != null
            && loan.ScheduleDueDay.HasValue
            && loan.ScheduleStartDate.HasValue
            ? LoanScheduleStatus.Complete
            : LoanScheduleStatus.Incomplete;
    }

    private async Task<LoanResult?> ValidateRecurringPaymentLinkAsync(
        string recurringPaymentId,
        string loanId,
        CancellationToken cancellationToken,
        bool allowMissingExistingLink = false)
    {
        var paymentExists = await _context.RecurringPayments
            .AsNoTracking()
            .AnyAsync(payment => payment.Id == recurringPaymentId, cancellationToken);
        if (!paymentExists && !allowMissingExistingLink)
        {
            return new LoanResult(
                LoanMutationStatus.RecurringPaymentNotFound,
                Message: "Choose an existing recurring bill for this loan.");
        }

        var linked = await _context.Loans
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id != loanId
                && candidate.RecurringPaymentId == recurringPaymentId, cancellationToken);
        return linked
            ? new LoanResult(
                LoanMutationStatus.RecurringPaymentAlreadyLinked,
                Message: "That recurring bill is already linked to another loan.")
            : null;
    }

    private static LoanResult? Validate(Loan loan, bool validateTerm = true)
    {
        if (string.IsNullOrWhiteSpace(loan.Name))
            return Invalid("Name is required.");
        if (string.IsNullOrWhiteSpace(loan.RecurringPaymentId))
            return Invalid("Choose a recurring bill.");
        if (loan.OpeningPrincipal <= 0m)
            return Invalid("Opening principal must be greater than zero.");
        if (loan.TrackingStartDate == default)
            return Invalid("Tracking start date is required.");
        if (loan.AnnualRatePercent is < 0m or > 100m)
            return Invalid("Annual rate must be between 0% and 100%.");
        if (validateTerm && loan.TermPeriods is < 1 or > 360)
            return Invalid("Term must be between 1 and 360 payment periods.");
        if (!LoanInterestMethod.IsKnown(loan.InterestMethod))
            return Invalid("Interest method is not supported.");
        if (!LoanRateBasis.IsKnown(loan.RateBasis))
            return Invalid("Interest rate period is not supported.");
        return null;
    }

    private static LoanResult Invalid(string message) =>
        new(LoanMutationStatus.Invalid, Message: message);

    private async Task<LoanResult?> SynchronizeNewLinkTermAsync(
        Loan loan,
        RecurringPayment payment,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(payment.EndDate))
        {
            if (!DateOnly.TryParseExact(payment.EndDate, "yyyy-MM-dd", out var endDate)
                || !LoanTermSchedule.TryCountPaymentsThrough(loan, endDate, out var count))
            {
                return Invalid("The linked bill end date must include between 1 and 360 loan payments.");
            }
            loan.TermPeriods = count;
            return null;
        }

        if (LoanTermSchedule.TryGetEndDate(loan, out var calculatedEndDate))
        {
            await _occurrences.PreserveThroughTodayAndResetFutureAsync(
                payment,
                cancellationToken: cancellationToken);
            payment.EndDate = calculatedEndDate.ToString("yyyy-MM-dd");
        }
        return null;
    }
}
