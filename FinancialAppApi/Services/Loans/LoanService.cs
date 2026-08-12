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
    LoanReplayResult Replay);

/// <summary>
/// Stores loan terms and builds the read model by replaying the linked bill's complete settlement
/// history. No balance is persisted: deleting or restoring a ledger row changes the replay only.
/// </summary>
public sealed class LoanService
{
    private readonly AppDbContext _context;

    public LoanService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<List<LoanView>> GetLoansAsync(CancellationToken cancellationToken = default)
    {
        var loans = await _context.Loans
            .AsNoTracking()
            .OrderBy(loan => loan.Name)
            .ThenBy(loan => loan.Id)
            .ToListAsync(cancellationToken);
        return await BuildViewsAsync(loans, cancellationToken);
    }

    public async Task<LoanView?> GetLoanAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var loan = await _context.Loans
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (loan == null) return null;
        return (await BuildViewsAsync([loan], cancellationToken)).Single();
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

        var validation = Validate(loan);
        if (validation != null) return validation;

        var paymentStatus = await ValidateRecurringPaymentLinkAsync(
            loan.RecurringPaymentId,
            loan.Id,
            cancellationToken);
        if (paymentStatus != null) return paymentStatus;

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

        var validation = Validate(updated);
        if (validation != null) return validation;

        var paymentStatus = await ValidateRecurringPaymentLinkAsync(
            updated.RecurringPaymentId,
            id,
            cancellationToken,
            allowMissingExistingLink: string.Equals(
                updated.RecurringPaymentId,
                loan.RecurringPaymentId,
                StringComparison.Ordinal));
        if (paymentStatus != null) return paymentStatus;

        loan.Name = updated.Name.Trim();
        loan.RecurringPaymentId = updated.RecurringPaymentId;
        loan.OpeningPrincipal = updated.OpeningPrincipal;
        loan.TrackingStartDate = updated.TrackingStartDate;
        loan.AnnualRatePercent = updated.AnnualRatePercent;
        loan.TermPeriods = updated.TermPeriods;
        loan.InterestMethod = updated.InterestMethod;

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
                && transaction.RecurringOccurrenceDate != null
                && transaction.RecurringOccurrenceDate >= earliestTrackingStart)
            .ToListAsync(cancellationToken);

        return loans.Select(loan =>
        {
            var inputs = transactions
                .Where(transaction => transaction.RecurringPaymentId == loan.RecurringPaymentId)
                .Select(transaction => new LoanPaymentInput(
                    transaction.RecurringOccurrenceDate!.Value,
                    transaction.PostedAt,
                    transaction.Amount,
                    string.Equals(transaction.LedgerCategory, "Discarded", StringComparison.OrdinalIgnoreCase),
                    transaction.Id))
                .ToList();

            payments.TryGetValue(loan.RecurringPaymentId, out var payment);
            var frequency = payment?.Frequency;
            return new LoanView(loan, payment, LoanReplay.Replay(loan, frequency, inputs, payment?.DueDate));
        }).ToList();
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

    private static LoanResult? Validate(Loan loan)
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
        if (loan.TermPeriods is < 1 or > 360)
            return Invalid("Term must be between 1 and 360 payment periods.");
        if (loan.InterestMethod is not LoanInterestMethod.ReducingBalance and not LoanInterestMethod.Flat)
            return Invalid("Interest method is not supported.");
        return null;
    }

    private static LoanResult Invalid(string message) =>
        new(LoanMutationStatus.Invalid, Message: message);
}
