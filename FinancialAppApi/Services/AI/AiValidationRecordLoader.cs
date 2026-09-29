using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

// Record loads action validation needs: the recurring payments and wishlist items a proposed
// action may target, read fresh so a validated id always names a record that still exists.
public partial class AiAssistantService
{
    internal sealed record AiRecurringRow(
        string Id, string Name, decimal Amount, string Category, string LedgerCategory,
        string StartDate, string? EndDate, int DueDate, bool Active,
        string Frequency, string NextDueDate, bool PushReminderEnabled = false,
        string PushReminderMode = "Once", int PushReminderLeadDays = 1,
        string PaymentMode = Models.RecurringPaymentMode.Manual,
        string? LinkedLoanId = null, string? LinkedLoanName = null);

    // The next due date is DERIVED here, from the same occurrence ledger the Recurring view reads
    // through RecurringPaymentsController.BuildRecurringPaymentDtosAsync -- never from the stored
    // `RecurringPayment.NextDueDate` column. That column is a legacy cache the settlement and edit
    // paths deliberately null out, so reading it handed the assistant an empty string for nearly
    // every bill and it correctly, uselessly, answered "not available" about dates the user could
    // see on screen. Deriving it also keeps the two answers from disagreeing, which would be worse.
    private async Task<List<AiRecurringRow>> LoadRecurringRowsAsync(CancellationToken cancellationToken)
    {
        var payments = await (from r in _context.RecurringPayments.AsNoTracking()
            join loan in _context.Loans.AsNoTracking() on r.Id equals loan.RecurringPaymentId into linkedLoans
            from linkedLoan in linkedLoans.DefaultIfEmpty()
            orderby r.Name
            select new
            {
                Payment = r,
                LinkedLoanId = linkedLoan == null ? null : linkedLoan.Id,
                LinkedLoanName = linkedLoan == null ? null : linkedLoan.Name,
            })
            .Take(100)
            .ToListAsync(cancellationToken);
        if (payments.Count == 0) return [];

        // Inactive payments have no next occurrence by definition; the ledger filters them itself,
        // so their rows keep the empty string and read as "no upcoming date", not "unknown".
        var nextPending = await _recurringOccurrenceLedger.GetNextPendingAsync(
            payments.Select(entry => entry.Payment).ToList(),
            _financialClock.Today,
            includeFrom: true,
            cancellationToken);

        return payments.Select(entry =>
        {
            var r = entry.Payment;
            var nextDueDate = nextPending.TryGetValue(r.Id, out var occurrence)
                ? occurrence.OccurrenceDate.ToString("yyyy-MM-dd")
                : r.NextDueDate ?? string.Empty;
            return new AiRecurringRow(
                r.Id, r.Name, r.Amount, r.Category, r.LedgerCategory,
                r.StartDate, r.EndDate, r.DueDate, r.Active,
                r.Frequency, nextDueDate, r.PushReminderEnabled,
                r.PushReminderMode, r.PushReminderLeadDays, r.PaymentMode,
                entry.LinkedLoanId, entry.LinkedLoanName);
        }).ToList();
    }

    private async Task<List<AiWishlistRow>> LoadWishlistRowsAsync(
        int? itemId,
        CancellationToken cancellationToken)
    {
        var query = _context.WishlistItems
            .AsNoTracking()
            .OrderBy(w => w.IsPurchased)
            .ThenByDescending(w => w.IsActive)
            .ThenByDescending(w => w.CreatedAt)
            .AsQueryable();
        if (itemId is > 0) query = query.Where(w => w.Id == itemId);
        return await query
            .Select(w => new AiWishlistRow(w.Id, w.Name, w.Price, w.Priority, w.IsActive, w.IsPurchased, w.CreatedAt, w.PurchasedAt))
            .Take(100)
            .ToListAsync(cancellationToken);
    }
}
