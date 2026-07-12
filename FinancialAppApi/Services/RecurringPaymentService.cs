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

public class RecurringPaymentService
{
    private readonly AppDbContext _context;

    public RecurringPaymentService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<List<RecurringPayment>> GetRecurringPaymentsAsync()
    {
        // Read-only: results are mapped to DTOs by the controller and never mutated, so skip
        // change tracking. Recurring payments are inherently bounded (a handful per user), so no
        // pagination is applied here.
        return await _context.RecurringPayments
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync();
    }

    public async Task<CreateRecurringPaymentResult> CreateRecurringPaymentAsync(RecurringPayment payment)
    {
        // Idempotency: the offline outbox sends a client-generated id ("rec-...") and may replay
        // the same create on retry (e.g. the write committed but the response was lost during a
        // Cloud Run cold start). Return the existing row instead of inserting a duplicate PK
        // (which would 500) so a lost-response retry resolves as success rather than a false failure.
        if (!string.IsNullOrWhiteSpace(payment.Id))
        {
            var existing = await _context.RecurringPayments.FirstOrDefaultAsync(p => p.Id == payment.Id);
            if (existing != null)
            {
                return new CreateRecurringPaymentResult(CreateRecurringPaymentStatus.Existing, existing);
            }
        }

        if (!await CategoryExistsAsync(payment.Category))
        {
            return new CreateRecurringPaymentResult(
                CreateRecurringPaymentStatus.InvalidCategory,
                Message: $"Category '{payment.Category}' does not exist.");
        }

        _context.RecurringPayments.Add(payment);
        await _context.SaveChangesAsync();
        return new CreateRecurringPaymentResult(CreateRecurringPaymentStatus.Created, payment);
    }

    public async Task<RecurringPayment?> ToggleActiveAsync(string id, bool? active = null)
    {
        var payment = await _context.RecurringPayments.FindAsync(id);
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
            payment.Active = desired;
            await _context.SaveChangesAsync();
        }

        return payment;
    }

    public async Task<UpdateRecurringPaymentResult> UpdateRecurringPaymentAsync(string id, RecurringPayment updated)
    {
        var existing = await _context.RecurringPayments.FindAsync(id);
        if (existing == null)
        {
            return new UpdateRecurringPaymentResult(UpdateRecurringPaymentStatus.NotFound);
        }

        if (!await CategoryExistsAsync(updated.Category))
        {
            return new UpdateRecurringPaymentResult(
                UpdateRecurringPaymentStatus.InvalidCategory,
                Message: $"Category '{updated.Category}' does not exist.");
        }

        existing.Name = updated.Name;
        existing.Amount = updated.Amount;
        existing.Frequency = updated.Frequency;
        existing.Category = updated.Category;
        existing.LedgerCategory = updated.LedgerCategory;
        existing.NextDueDate = updated.NextDueDate;
        existing.DueDate = updated.DueDate;
        existing.StartDate = updated.StartDate;
        existing.EndDate = updated.EndDate;
        existing.Active = updated.Active;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await _context.RecurringPayments.AnyAsync(e => e.Id == id))
            {
                return new UpdateRecurringPaymentResult(UpdateRecurringPaymentStatus.NotFound);
            }

            throw;
        }

        return new UpdateRecurringPaymentResult(UpdateRecurringPaymentStatus.Updated, existing);
    }

    public async Task<bool> DeleteRecurringPaymentAsync(string id)
    {
        var payment = await _context.RecurringPayments.FindAsync(id);
        if (payment == null)
        {
            return false;
        }

        _context.RecurringPayments.Remove(payment);
        await _context.SaveChangesAsync();

        return true;
    }

    private async Task<bool> CategoryExistsAsync(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return false;
        }

        if (TransactionCategoryService.IsReservedName(category))
        {
            return true;
        }

        return await _context.TransactionCategories.AnyAsync(c => c.Name.ToLower() == category.Trim().ToLower());
    }
}
