using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public enum UpdateRecurringPaymentStatus
{
    Updated,
    NotFound,
    InvalidCategory
}

public sealed record UpdateRecurringPaymentResult(
    UpdateRecurringPaymentStatus Status,
    RecurringPayment? Payment = null,
    string? Message = null);

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
    InvalidCategory
}

public sealed record CreateRecurringPaymentResult(
    CreateRecurringPaymentStatus Status,
    RecurringPayment? Payment = null,
    string? Message = null);

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
    string PaymentMode);

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
        return await _context.RecurringPayments
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Id)
            .Select(p => new RecurringPaymentProjection(
                p.Id,
                p.Name,
                p.Amount,
                p.Frequency,
                p.Category,
                p.LedgerCategory,
                p.NextDueDate,
                p.DueDate,
                p.StartDate,
                p.Active,
                p.EndDate,
                p.PushReminderEnabled,
                p.PushReminderMode,
                p.PushReminderLeadDays,
                p.OccurrenceTrackingStartDate,
                p.PaymentMode
            ))
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

    public async Task<RecurringPayment?> ToggleActiveAsync(string id, bool? active = null, CancellationToken cancellationToken = default)
    {
        var payment = await _context.RecurringPayments.FindAsync([id], cancellationToken);
        if (payment == null)
        {
            return null;
        }

        // Prefer the absolute desired state sent by the client. The offline outbox coalesces
        // multiple toggles into a single op carrying the final {active} value and may replay it
        // on retry, so a relative flip here would drift the persisted state (even-count coalescing
        // or a lost-response retry would land on the wrong value). Fall back to a relative flip only
        // for legacy callers that send no body.
        var desired = active ?? !payment.Active;
        if (payment.Active != desired)
        {
            await _occurrences.PreserveThroughTodayAndResetFutureAsync(
                payment,
                preserveThroughToday: !desired,
                cancellationToken);
            payment.Active = desired;
            if (desired) payment.OccurrenceTrackingStartDate = _clock.Today.AddDays(1);
            await _context.SaveChangesAsync(cancellationToken);
        }

        return payment;
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

    public async Task<bool> DeleteRecurringPaymentAsync(string id, CancellationToken cancellationToken = default)
    {
        var payment = await _context.RecurringPayments.FindAsync([id], cancellationToken);
        if (payment == null)
        {
            return false;
        }

        await _occurrences.PreserveThroughTodayAndResetFutureAsync(payment, cancellationToken: cancellationToken);
        _context.RecurringPayments.Remove(payment);
        await _context.SaveChangesAsync(cancellationToken);

        return true;
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
}
