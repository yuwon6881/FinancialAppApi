using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Accounts;
using FinancialAppApi.Services.Loans;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public enum UpdateRecurringPaymentStatus
{
    Updated,
    NotFound,
    InvalidCategory,
    InvalidLoanTerm,
    InvalidAccount
}

public sealed record UpdateRecurringPaymentResult(
    UpdateRecurringPaymentStatus Status,
    RecurringPayment? Payment = null,
    string? Message = null,
    string? Code = null,
    IReadOnlyList<string>? MissingBuckets = null);

public enum ToggleRecurringPaymentStatus
{
    Updated,
    NotFound,
    InvalidAccount
}

public sealed record ToggleRecurringPaymentResult(
    ToggleRecurringPaymentStatus Status,
    RecurringPayment? Payment = null,
    string? Message = null,
    string? Code = null,
    IReadOnlyList<string>? MissingBuckets = null);

public enum UpdateReminderStatus
{
    Updated,
    NotFound,
    InvalidMode,
    InvalidLeadDays
}

public sealed record UpdateReminderResult(
    UpdateReminderStatus Status,
    RecurringPayment? Payment = null,
    string? Message = null);

public enum CreateRecurringPaymentStatus
{
    Created,
    Existing,
    InvalidCategory,
    InvalidAccount
}

public sealed record CreateRecurringPaymentResult(
    CreateRecurringPaymentStatus Status,
    RecurringPayment? Payment = null,
    string? Message = null,
    string? Code = null,
    IReadOnlyList<string>? MissingBuckets = null);

public sealed record RecurringPaymentProjection(
    string Id,
    string Name,
    decimal Amount,
    string Frequency,
    string Category,
    string LedgerCategory,
    string? NextDueDate,
    int DueDate,
    string StartDate,
    bool Active,
    string? EndDate,
    bool ReminderEnabled,
    string ReminderMode,
    int ReminderLeadDays,
    DateOnly OccurrenceTrackingStartDate,
    string PaymentMode,
    string? LinkedLoanId,
    string? LinkedLoanName,
    string AccountId = "");

public enum DeleteRecurringPaymentStatus
{
    Deleted,
    NotFound,
    LinkedToLoan
}

public sealed record DeleteRecurringPaymentResult(
    DeleteRecurringPaymentStatus Status,
    string? LoanName = null);

public class RecurringPaymentService
{
    // The only lead-day windows the client can present, and the same set the database check
    // constraint enforces — keep both lists in sync if this ever changes.
    private static readonly int[] AllowedLeadDays = [1, 2, 3, 7];

    private readonly AppDbContext _context;
    private readonly RecurringOccurrenceLedgerService _occurrences;
    private readonly FinancialClock _clock;

    public RecurringPaymentService(
        AppDbContext context,
        RecurringOccurrenceLedgerService? occurrences = null,
        FinancialClock? clock = null)
    {
        _context = context;
        _clock = clock ?? FinancialClock.Utc;
        _occurrences = occurrences ?? new RecurringOccurrenceLedgerService(
            context,
            new RecurringOccurrenceService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RecurringOccurrenceService>.Instance),
            _clock);
    }

    public async Task<List<RecurringPaymentProjection>> GetRecurringPaymentsAsync(CancellationToken cancellationToken = default)
    {
        // Read-only: results are mapped to DTOs by the controller and never mutated, so skip
        // change tracking. Recurring payments are inherently bounded (a handful per user), so no
        // pagination is applied here.
        return await (
            from payment in _context.RecurringPayments.AsNoTracking()
            join loan in _context.Loans.AsNoTracking()
                on payment.Id equals loan.RecurringPaymentId into linkedLoans
            from linkedLoan in linkedLoans.DefaultIfEmpty()
            orderby payment.Name, payment.Id
            select new RecurringPaymentProjection(
                payment.Id,
                payment.Name,
                payment.Amount,
                payment.Frequency,
                payment.Category,
                payment.LedgerCategory,
                payment.NextDueDate,
                payment.DueDate,
                payment.StartDate,
                payment.Active,
                payment.EndDate,
                payment.PushReminderEnabled,
                payment.PushReminderMode,
                payment.PushReminderLeadDays,
                payment.OccurrenceTrackingStartDate,
                payment.PaymentMode,
                linkedLoan == null ? null : linkedLoan.Id,
                linkedLoan == null ? null : linkedLoan.Name,
                payment.AccountId))
            .ToListAsync(cancellationToken);
    }

    public async Task<CreateRecurringPaymentResult> CreateRecurringPaymentAsync(RecurringPayment payment, CancellationToken cancellationToken = default)
    {
        // Idempotency: the offline outbox sends a client-generated id ("rec-...") and may replay
        // the same create on retry (e.g. the write committed but the response was lost during a
        // Cloud Run cold start). Return the existing row instead of inserting a duplicate PK
        // (which would 500) so a lost-response retry resolves as success rather than a false failure.
        if (!string.IsNullOrWhiteSpace(payment.Id))
        {
            var existing = await _context.RecurringPayments.FirstOrDefaultAsync(p => p.Id == payment.Id, cancellationToken);
            if (existing != null)
            {
                return new CreateRecurringPaymentResult(CreateRecurringPaymentStatus.Existing, existing);
            }
        }

        if (!await CategoryExistsAsync(payment.Category, cancellationToken))
        {
            return new CreateRecurringPaymentResult(
                CreateRecurringPaymentStatus.InvalidCategory,
                Message: $"Category '{payment.Category}' does not exist.");
        }
        var accountValidation = await ValidateAccountAsync(payment, payment.Active, cancellationToken);
        if (accountValidation is not null)
            return new CreateRecurringPaymentResult(
                CreateRecurringPaymentStatus.InvalidAccount,
                Message: accountValidation.Value.Message,
                Code: accountValidation.Value.Code,
                MissingBuckets: MissingBucketsFor(payment.LedgerCategory));

        if (payment.OccurrenceTrackingStartDate == default)
        {
            var requestedStart = DateOnly.TryParseExact(payment.StartDate, "yyyy-MM-dd", out var start)
                ? start
                : _clock.Today;
            payment.OccurrenceTrackingStartDate = requestedStart > _clock.Today ? requestedStart : _clock.Today;
        }
        _context.RecurringPayments.Add(payment);
        await _context.SaveChangesAsync(cancellationToken);
        return new CreateRecurringPaymentResult(CreateRecurringPaymentStatus.Created, payment);
    }

    public async Task<ToggleRecurringPaymentResult> ToggleActiveAsync(string id, bool? active = null, CancellationToken cancellationToken = default)
    {
        var payment = await _context.RecurringPayments.FindAsync([id], cancellationToken);
        if (payment == null)
        {
            return new ToggleRecurringPaymentResult(ToggleRecurringPaymentStatus.NotFound);
        }

        // Prefer the absolute desired state sent by the client. The offline outbox coalesces
        // multiple toggles into a single op carrying the final {active} value and may replay it
        // on retry, so a relative flip here would drift the persisted state (even-count coalescing
        // or a lost-response retry would land on the wrong value). Fall back to a relative flip only
        // for legacy callers that send no body.
        var desired = active ?? !payment.Active;
        if (desired)
        {
            var accountValidation = await ValidateAccountAsync(payment, requireOpen: true, cancellationToken);
            if (accountValidation is not null)
                return new ToggleRecurringPaymentResult(
                    ToggleRecurringPaymentStatus.InvalidAccount,
                    payment,
                    accountValidation.Value.Message,
                    accountValidation.Value.Code,
                    MissingBucketsFor(payment.LedgerCategory));
        }
        if (payment.Active != desired)
        {
            await _occurrences.PreserveThroughTodayAndResetFutureAsync(
                payment,
                preserveThroughToday: !desired,
                cancellationToken);
            payment.Active = desired;
            if (desired) payment.OccurrenceTrackingStartDate = _clock.Today;
            await _context.SaveChangesAsync(cancellationToken);
        }

        return new ToggleRecurringPaymentResult(ToggleRecurringPaymentStatus.Updated, payment);
    }

    public async Task<UpdateRecurringPaymentResult> UpdateRecurringPaymentAsync(string id, RecurringPayment updated, CancellationToken cancellationToken = default)
    {
        var existing = await _context.RecurringPayments.FindAsync([id], cancellationToken);
        if (existing == null)
        {
            return new UpdateRecurringPaymentResult(UpdateRecurringPaymentStatus.NotFound);
        }

        if (!await CategoryExistsAsync(updated.Category, cancellationToken))
        {
            return new UpdateRecurringPaymentResult(
                UpdateRecurringPaymentStatus.InvalidCategory,
                Message: $"Category '{updated.Category}' does not exist.");
        }
        var accountValidation = await ValidateAccountAsync(updated, updated.Active, cancellationToken);
        if (accountValidation is not null)
            return new UpdateRecurringPaymentResult(
                UpdateRecurringPaymentStatus.InvalidAccount,
                existing,
                accountValidation.Value.Message,
                accountValidation.Value.Code,
                MissingBucketsFor(updated.LedgerCategory));

        var linkedLoan = await _context.Loans
            .FirstOrDefaultAsync(loan => loan.RecurringPaymentId == id, cancellationToken);
        if (linkedLoan != null)
        {
            if (!string.IsNullOrWhiteSpace(updated.EndDate))
            {
                if (!DateOnly.TryParseExact(updated.EndDate, "yyyy-MM-dd", out var endDate)
                    || !LoanTermSchedule.TryCountPaymentsThrough(linkedLoan, endDate, out var count))
                {
                    return new UpdateRecurringPaymentResult(
                        UpdateRecurringPaymentStatus.InvalidLoanTerm,
                        Message: "The end date must include between 1 and 360 loan payments.");
                }
                linkedLoan.TermPeriods = count;
            }
            else if (LoanTermSchedule.TryGetEndDate(linkedLoan, out var calculatedEndDate))
            {
                updated.EndDate = calculatedEndDate.ToString("yyyy-MM-dd");
            }
        }

        await _occurrences.PreserveThroughTodayAndResetFutureAsync(existing, cancellationToken: cancellationToken);

        existing.Name = updated.Name;
        existing.Amount = updated.Amount;
        existing.Frequency = updated.Frequency;
        existing.Category = updated.Category;
        existing.LedgerCategory = updated.LedgerCategory;
        existing.NextDueDate = null;
        existing.DueDate = updated.DueDate;
        existing.StartDate = updated.StartDate;
        existing.EndDate = updated.EndDate;
        existing.Active = updated.Active;
        existing.PaymentMode = updated.PaymentMode;
        existing.AccountId = updated.AccountId.Trim();

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await _context.RecurringPayments.AnyAsync(e => e.Id == id, cancellationToken))
            {
                return new UpdateRecurringPaymentResult(UpdateRecurringPaymentStatus.NotFound);
            }

            throw;
        }

        return new UpdateRecurringPaymentResult(UpdateRecurringPaymentStatus.Updated, existing);
    }

    public async Task<DeleteRecurringPaymentResult> DeleteRecurringPaymentAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var payment = await _context.RecurringPayments.FindAsync([id], cancellationToken);
        if (payment == null)
        {
            return new DeleteRecurringPaymentResult(DeleteRecurringPaymentStatus.NotFound);
        }

        var linkedLoanName = await _context.Loans
            .AsNoTracking()
            .Where(loan => loan.RecurringPaymentId == id)
            .Select(loan => loan.Name)
            .FirstOrDefaultAsync(cancellationToken);
        if (linkedLoanName != null)
        {
            return new DeleteRecurringPaymentResult(
                DeleteRecurringPaymentStatus.LinkedToLoan,
                linkedLoanName);
        }

        await _occurrences.PreserveThroughTodayAndResetFutureAsync(payment, cancellationToken: cancellationToken);
        _context.RecurringPayments.Remove(payment);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return new DeleteRecurringPaymentResult(DeleteRecurringPaymentStatus.LinkedToLoan);
        }

        return new DeleteRecurringPaymentResult(DeleteRecurringPaymentStatus.Deleted);
    }

    public async Task<UpdateReminderResult> UpdateReminderAsync(
        string id,
        bool enabled,
        string mode,
        int leadDays,
        CancellationToken cancellationToken = default)
    {
        var payment = await _context.RecurringPayments.FindAsync([id], cancellationToken);
        if (payment == null)
        {
            return new UpdateReminderResult(UpdateReminderStatus.NotFound);
        }

        if (!TryNormalizeReminderMode(mode, out var normalizedMode))
        {
            return new UpdateReminderResult(
                UpdateReminderStatus.InvalidMode,
                Message: "Mode must be Once or Daily.");
        }

        if (!AllowedLeadDays.Contains(leadDays))
        {
            return new UpdateReminderResult(
                UpdateReminderStatus.InvalidLeadDays,
                Message: "Lead days must be one of: " + string.Join(", ", AllowedLeadDays) + ".");
        }

        payment.PushReminderEnabled = enabled;
        payment.PushReminderMode = normalizedMode;
        payment.PushReminderLeadDays = leadDays;
        await _context.SaveChangesAsync(cancellationToken);

        return new UpdateReminderResult(UpdateReminderStatus.Updated, payment);
    }

    private static bool TryNormalizeReminderMode(string? value, out string mode)
    {
        if (string.Equals(value, "Once", StringComparison.OrdinalIgnoreCase))
        {
            mode = "Once";
            return true;
        }
        if (string.Equals(value, "Daily", StringComparison.OrdinalIgnoreCase))
        {
            mode = "Daily";
            return true;
        }

        mode = string.Empty;
        return false;
    }

    private async Task<bool> CategoryExistsAsync(string category, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return false;
        }

        if (TransactionCategoryService.IsReservedName(category))
        {
            return true;
        }

        return await _context.TransactionCategories.AnyAsync(c => c.Name.ToLower() == category.Trim().ToLower(), cancellationToken);
    }

    private async Task<(string Code, string Message)?> ValidateAccountAsync(
        RecurringPayment payment,
        bool requireOpen,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payment.AccountId))
            return ("ledger_account_required", "Choose an account for this recurring payment.");
        if (!LedgerAccountPlacement.IsBucket(payment.LedgerCategory))
            return ("ledger_account_invalid", "A recurring payment must use one of the four ledger buckets.");

        var account = await _context.LedgerAccounts.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == payment.AccountId.Trim(), cancellationToken);
        if (account is null
            || !account.Bucket.Equals(payment.LedgerCategory.Trim(), StringComparison.OrdinalIgnoreCase)
            || (requireOpen && account.IsArchived))
            return ("ledger_account_invalid", "Choose an open account in the recurring payment's bucket.");
        return null;
    }

    private static IReadOnlyList<string>? MissingBucketsFor(string? ledgerCategory)
    {
        var bucket = LedgerAccountPlacement.BucketOrNull(ledgerCategory);
        return bucket is null ? null : [bucket];
    }
}
