using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/recurring-payments")]
[AuthorizeToken]
public class RecurringPaymentsController : ControllerBase
{
    private readonly AppDbContext _context;

    public RecurringPaymentsController(AppDbContext context)
    {
        _context = context;
    }

    // GET: api/recurring-payments
    [HttpGet]
    public async Task<ActionResult<IEnumerable<RecurringPayment>>> GetRecurringPayments()
    {
        return await _context.RecurringPayments.ToListAsync();
    }

    // POST: api/recurring-payments
    [HttpPost]
    public async Task<ActionResult<RecurringPayment>> PostRecurringPayment(RecurringPayment payment)
    {
        if (string.IsNullOrWhiteSpace(payment.Id))
        {
            payment.Id = $"rec-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        }

        _context.RecurringPayments.Add(payment);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetRecurringPayments), new { id = payment.Id }, payment);
    }

    // PUT: api/recurring-payments/{id}/toggle
    [HttpPut("{id}/toggle")]
    public async Task<IActionResult> ToggleActive(string id)
    {
        var payment = await _context.RecurringPayments.FindAsync(id);
        if (payment == null)
        {
            return NotFound();
        }

        payment.Active = !payment.Active;
        await _context.SaveChangesAsync();

        return Ok(payment);
    }

    // PUT: api/recurring-payments/{id}
    [HttpPut("{id}")]
    public async Task<IActionResult> PutRecurringPayment(string id, RecurringPayment payment)
    {
        if (id != payment.Id)
        {
            return BadRequest("ID mismatch");
        }

        var existing = await _context.RecurringPayments.FindAsync(id);
        if (existing == null)
        {
            return NotFound();
        }

        existing.Name = payment.Name;
        existing.Amount = payment.Amount;
        existing.Frequency = payment.Frequency;
        existing.Category = payment.Category;
        existing.LedgerCategory = payment.LedgerCategory;
        existing.NextDueDate = payment.NextDueDate;
        existing.DueDate = payment.DueDate;
        existing.StartDate = payment.StartDate;
        existing.EndDate = payment.EndDate;
        existing.Active = payment.Active;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await _context.RecurringPayments.AnyAsync(e => e.Id == id))
            {
                return NotFound();
            }
            else
            {
                throw;
            }
        }

        return Ok(existing);
    }

    // DELETE: api/recurring-payments/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteRecurringPayment(string id)
    {
        var payment = await _context.RecurringPayments.FindAsync(id);
        if (payment == null)
        {
            return NotFound();
        }

        _context.RecurringPayments.Remove(payment);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    public class DismissDto
    {
        public string Id { get; set; } = string.Empty; // Format: "{rpId}-{year}-{month}"
    }

    // POST: api/recurring-payments/dismiss
    [HttpPost("dismiss")]
    public async Task<IActionResult> DismissRecurringPayment([FromBody] DismissDto dto)
    {
        if (string.IsNullOrEmpty(dto.Id)) return BadRequest("Invalid ID");
        
        var parts = dto.Id.Split('-');
        if (parts.Length < 3) return BadRequest("Invalid ID format");
        
        var yearStr = parts[parts.Length - 2];
        var monthStr = parts[parts.Length - 1];
        
        if (!int.TryParse(yearStr, out var year) || !int.TryParse(monthStr, out var month))
        {
            return BadRequest("Invalid year/month in ID");
        }
        
        var rpId = string.Join("-", parts.Take(parts.Length - 2));

        var exists = await _context.RecurringPaymentDismissals.AnyAsync(d => d.Id == dto.Id);
        if (!exists)
        {
            var dismissal = new RecurringPaymentDismissal
            {
                Id = dto.Id,
                RecurringPaymentId = rpId,
                Year = year,
                Month = month,
                DismissedAt = DateTime.UtcNow.ToString("yyyy-MM-dd")
            };
            _context.RecurringPaymentDismissals.Add(dismissal);
            await _context.SaveChangesAsync();
        }

        return Ok();
    }
}
