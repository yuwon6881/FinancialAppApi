using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public enum UpdateRecurringPaymentStatus
{
    Updated,
    NotFound
}

public sealed record UpdateRecurringPaymentResult(
    UpdateRecurringPaymentStatus Status,
    RecurringPayment? Payment = null);

public class RecurringPaymentService
{
    private readonly AppDbContext _context;

    public RecurringPaymentService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<List<RecurringPayment>> GetRecurringPaymentsAsync()
    {
        return await _context.RecurringPayments.ToListAsync();
    }

    public async Task<RecurringPayment> CreateRecurringPaymentAsync(RecurringPayment payment)
    {
        _context.RecurringPayments.Add(payment);
        await _context.SaveChangesAsync();
        return payment;
    }

    public async Task<RecurringPayment?> ToggleActiveAsync(string id)
    {
        var payment = await _context.RecurringPayments.FindAsync(id);
        if (payment == null)
        {
            return null;
        }

        payment.Active = !payment.Active;
        await _context.SaveChangesAsync();

        return payment;
    }

    public async Task<UpdateRecurringPaymentResult> UpdateRecurringPaymentAsync(string id, RecurringPayment updated)
    {
        var existing = await _context.RecurringPayments.FindAsync(id);
        if (existing == null)
        {
            return new UpdateRecurringPaymentResult(UpdateRecurringPaymentStatus.NotFound);
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
}
