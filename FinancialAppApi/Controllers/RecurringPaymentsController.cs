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
        var list = await _recurringPaymentService.GetRecurringPaymentsAsync(HttpContext.RequestAborted);
        return Ok(list.Select(MapToDto).ToList());
    }

    // POST: api/recurring-payments
    [HttpPost]
    public async Task<ActionResult<RecurringPaymentDto>> PostRecurringPayment(RecurringPaymentDto dto)
    {
        if (!TryNormalizeFrequency(dto.Frequency, out var frequency))
        {
            return BadRequest(new { message = "Frequency must be Monthly or Annually." });
        }
        if (!ObfuscationHelper.TryDeobfuscate(dto.Amount, out var decodedAmount) || decodedAmount == 0m)
        {
            return BadRequest(new { message = "Amount must be a valid non-zero value." });
        }

        var payment = new RecurringPayment
        {
            Id = string.IsNullOrWhiteSpace(dto.Id) ? $"rec-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" : dto.Id,
            Name = dto.Name,
            Amount = Math.Round(decodedAmount, 2, MidpointRounding.AwayFromZero),
            Frequency = frequency,
            Category = dto.Category,
            LedgerCategory = dto.LedgerCategory,
            NextDueDate = dto.NextDueDate,
            DueDate = dto.DueDate,
            StartDate = dto.StartDate,
            Active = dto.Active,
            EndDate = dto.EndDate
        };

        var result = await _recurringPaymentService.CreateRecurringPaymentAsync(payment, HttpContext.RequestAborted);
        if (result.Status == CreateRecurringPaymentStatus.InvalidCategory)
        {
            return BadRequest(new { message = result.Message });
        }
        if (result.Status == CreateRecurringPaymentStatus.Existing)
        {
            // Idempotent replay of an already-committed create — return the stored row as success.
            return Ok(MapToDto(result.Payment!));
        }

        return CreatedAtAction(nameof(GetRecurringPayments), new { id = payment.Id }, MapToDto(payment));
    }

    // PUT: api/recurring-payments/{id}/toggle
    [HttpPut("{id}/toggle")]
    public async Task<IActionResult> ToggleActive(string id, [FromBody] ToggleActiveDto? dto = null)
    {
        var payment = await _recurringPaymentService.ToggleActiveAsync(id, dto?.Active, HttpContext.RequestAborted);
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

        if (!TryNormalizeFrequency(dto.Frequency, out var frequency))
        {
            return BadRequest(new { message = "Frequency must be Monthly or Annually." });
        }
        if (!ObfuscationHelper.TryDeobfuscate(dto.Amount, out var decodedAmount) || decodedAmount == 0m)
        {
            return BadRequest(new { message = "Amount must be a valid non-zero value." });
        }

        var updated = new RecurringPayment
        {
            Id = dto.Id,
            Name = dto.Name,
            Amount = Math.Round(decodedAmount, 2, MidpointRounding.AwayFromZero),
            Frequency = frequency,
            Category = dto.Category,
            LedgerCategory = dto.LedgerCategory,
            NextDueDate = dto.NextDueDate,
            DueDate = dto.DueDate,
            StartDate = dto.StartDate,
            EndDate = dto.EndDate,
            Active = dto.Active
        };

        var result = await _recurringPaymentService.UpdateRecurringPaymentAsync(id, updated, HttpContext.RequestAborted);
        if (result.Status == UpdateRecurringPaymentStatus.NotFound)
        {
            return NotFound();
        }
        if (result.Status == UpdateRecurringPaymentStatus.InvalidCategory)
        {
            return BadRequest(new { message = result.Message });
        }

        return Ok(MapToDto(result.Payment!));
    }

    // DELETE: api/recurring-payments/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteRecurringPayment(string id)
    {
        var deleted = await _recurringPaymentService.DeleteRecurringPaymentAsync(id, HttpContext.RequestAborted);
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

    private static bool TryNormalizeFrequency(string? value, out string frequency)
    {
        if (string.Equals(value, "Monthly", StringComparison.OrdinalIgnoreCase))
        {
            frequency = "Monthly";
            return true;
        }
        if (string.Equals(value, "Annually", StringComparison.OrdinalIgnoreCase))
        {
            frequency = "Annually";
            return true;
        }

        frequency = string.Empty;
        return false;
    }

    private static RecurringPaymentDto MapToDto(RecurringPaymentProjection rp)
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

public class ToggleActiveDto
{
    public bool? Active { get; set; }
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
