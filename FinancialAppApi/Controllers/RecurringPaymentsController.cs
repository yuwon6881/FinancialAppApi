using Microsoft.AspNetCore.Mvc;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;
using FinancialAppApi.Services;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/recurring-payments")]
[AuthorizeToken]
public class RecurringPaymentsController : ControllerBase
{
    private readonly RecurringPaymentService _recurringPaymentService;

    public RecurringPaymentsController(RecurringPaymentService recurringPaymentService)
    {
        _recurringPaymentService = recurringPaymentService;
    }

    // GET: api/recurring-payments
    [HttpGet]
    public async Task<ActionResult<IEnumerable<RecurringPaymentDto>>> GetRecurringPayments()
    {
        var list = await _recurringPaymentService.GetRecurringPaymentsAsync();
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
            Amount = Math.Round(ObfuscationHelper.Deobfuscate(dto.Amount), 2, MidpointRounding.AwayFromZero),
            Frequency = dto.Frequency,
            Category = dto.Category,
            LedgerCategory = dto.LedgerCategory,
            NextDueDate = dto.NextDueDate,
            DueDate = dto.DueDate,
            StartDate = dto.StartDate,
            Active = dto.Active,
            EndDate = dto.EndDate
        };

        await _recurringPaymentService.CreateRecurringPaymentAsync(payment);

        return CreatedAtAction(nameof(GetRecurringPayments), new { id = payment.Id }, MapToDto(payment));
    }

    // PUT: api/recurring-payments/{id}/toggle
    [HttpPut("{id}/toggle")]
    public async Task<IActionResult> ToggleActive(string id)
    {
        var payment = await _recurringPaymentService.ToggleActiveAsync(id);
        if (payment == null)
        {
            return NotFound();
        }

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

        var updated = new RecurringPayment
        {
            Id = dto.Id,
            Name = dto.Name,
            Amount = Math.Round(ObfuscationHelper.Deobfuscate(dto.Amount), 2, MidpointRounding.AwayFromZero),
            Frequency = dto.Frequency,
            Category = dto.Category,
            LedgerCategory = dto.LedgerCategory,
            NextDueDate = dto.NextDueDate,
            DueDate = dto.DueDate,
            StartDate = dto.StartDate,
            EndDate = dto.EndDate,
            Active = dto.Active
        };

        var result = await _recurringPaymentService.UpdateRecurringPaymentAsync(id, updated);
        if (result.Status == UpdateRecurringPaymentStatus.NotFound)
        {
            return NotFound();
        }

        return Ok(MapToDto(result.Payment!));
    }

    // DELETE: api/recurring-payments/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteRecurringPayment(string id)
    {
        var deleted = await _recurringPaymentService.DeleteRecurringPaymentAsync(id);
        if (!deleted)
        {
            return NotFound();
        }

        return NoContent();
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
