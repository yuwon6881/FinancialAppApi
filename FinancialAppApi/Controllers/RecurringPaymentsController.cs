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
    public async Task<ActionResult<IEnumerable<RecurringPaymentDto>>> GetRecurringPayments()
    {
        var list = await _context.RecurringPayments.ToListAsync();
        return Ok(list.Select(MapToDto).ToList());
    }

    // POST: api/recurring-payments
    [HttpPost]
    public async Task<ActionResult<RecurringPaymentDto>> PostRecurringPayment(RecurringPaymentDto dto)
    {
        var payment = new RecurringPayment
        {
            Id = string.IsNullOrWhiteSpace(dto.Id) ? $"rec-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" : dto.Id,
            Name = dto.Name,
            Amount = ObfuscationHelper.Deobfuscate(dto.Amount),
            Frequency = dto.Frequency,
            Category = dto.Category,
            LedgerCategory = dto.LedgerCategory,
            NextDueDate = dto.NextDueDate,
            DueDate = dto.DueDate,
            StartDate = dto.StartDate,
            Active = dto.Active,
            EndDate = dto.EndDate
        };

        _context.RecurringPayments.Add(payment);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetRecurringPayments), new { id = payment.Id }, MapToDto(payment));
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

        return Ok(MapToDto(payment));
    }

    // PUT: api/recurring-payments/{id}
    [HttpPut("{id}")]
    public async Task<IActionResult> PutRecurringPayment(string id, RecurringPaymentDto dto)
    {
        if (id != dto.Id)
        {
            return BadRequest("ID mismatch");
        }

        var existing = await _context.RecurringPayments.FindAsync(id);
        if (existing == null)
        {
            return NotFound();
        }

        existing.Name = dto.Name;
        existing.Amount = ObfuscationHelper.Deobfuscate(dto.Amount);
        existing.Frequency = dto.Frequency;
        existing.Category = dto.Category;
        existing.LedgerCategory = dto.LedgerCategory;
        existing.NextDueDate = dto.NextDueDate;
        existing.DueDate = dto.DueDate;
        existing.StartDate = dto.StartDate;
        existing.EndDate = dto.EndDate;
        existing.Active = dto.Active;

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

        return Ok(MapToDto(existing));
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

    private static RecurringPaymentDto MapToDto(RecurringPayment rp)
    {
        return new RecurringPaymentDto
        {
            Id = rp.Id,
            Name = rp.Name,
            Amount = ObfuscationHelper.Obfuscate(rp.Amount),
            Frequency = rp.Frequency,
            Category = rp.Category,
            LedgerCategory = rp.LedgerCategory,
            NextDueDate = rp.NextDueDate,
            DueDate = rp.DueDate,
            StartDate = rp.StartDate,
            Active = rp.Active,
            EndDate = rp.EndDate
        };
    }
}

public class RecurringPaymentDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Amount { get; set; } = string.Empty;
    public string Frequency { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string LedgerCategory { get; set; } = string.Empty;
    public string NextDueDate { get; set; } = string.Empty;
    public int DueDate { get; set; }
    public string StartDate { get; set; } = string.Empty;
    public bool Active { get; set; }
    public string? EndDate { get; set; }
}
