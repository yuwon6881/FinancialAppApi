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
    private readonly RecurringPaymentPayEarlyService _payEarlyService;
    private readonly RecurringOccurrenceSettlementService? _settlementService;

    public RecurringPaymentsController(
        RecurringPaymentService recurringPaymentService,
        RecurringPaymentPayEarlyService payEarlyService,
        RecurringOccurrenceSettlementService? settlementService = null)
    {
        _recurringPaymentService = recurringPaymentService;
        _payEarlyService = payEarlyService;
        _settlementService = settlementService;
    }

    // GET: api/recurring-payments
    [HttpGet]
    public async Task<ActionResult<IEnumerable<RecurringPaymentDto>>> GetRecurringPayments()
    {
        return Ok(await BuildRecurringPaymentDtosAsync(
            _recurringPaymentService, _payEarlyService, HttpContext.RequestAborted));
    }

    // Shared with BootstrapController so the composite boot payload cannot drift from what
    // this endpoint returns. Kept here, next to the mapper it uses.
    internal static async Task<List<RecurringPaymentDto>> BuildRecurringPaymentDtosAsync(
        RecurringPaymentService recurringPaymentService,
        RecurringPaymentPayEarlyService payEarlyService,
        CancellationToken cancellationToken)
    {
        var list = await recurringPaymentService.GetRecurringPaymentsAsync(cancellationToken);
        // Resolved for the whole list at once: per-payment lookups issued three queries each, and
        // this runs on every /api/bootstrap, which every outbox drain re-fetches.
        var nextUnpaid = await payEarlyService.GetNextUnpaidOccurrencesAsync(list, cancellationToken);
        var result = new List<RecurringPaymentDto>(list.Count);
        foreach (var payment in list)
        {
            var dto = MapToDto(payment);
            if (nextUnpaid.TryGetValue(payment.Id, out var occurrence))
                dto.NextDueDate = occurrence.ToString("yyyy-MM-dd");
            result.Add(dto);
        }
        return result;
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
        if (!TryNormalizePaymentMode(dto.PaymentMode, out var paymentMode))
        {
            return BadRequest(new { message = "Payment mode must be AutoDeduct or Manual." });
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
            EndDate = dto.EndDate,
            PaymentMode = paymentMode
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
        if (!TryNormalizePaymentMode(dto.PaymentMode, out var paymentMode))
        {
            return BadRequest(new { message = "Payment mode must be AutoDeduct or Manual." });
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
            Active = dto.Active,
            PaymentMode = paymentMode
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

    // PUT: api/recurring-payments/{id}/reminder
    [HttpPut("{id}/reminder")]
    public async Task<IActionResult> PutReminder(string id, RecurringPaymentReminderDto dto)
    {
        var result = await _recurringPaymentService.UpdateReminderAsync(
            id, dto.Enabled, dto.Mode, dto.LeadDays, HttpContext.RequestAborted);

        if (result.Status == UpdateReminderStatus.NotFound)
        {
            return NotFound();
        }
        if (result.Status is UpdateReminderStatus.InvalidMode or UpdateReminderStatus.InvalidLeadDays)
        {
            return BadRequest(new { message = result.Message });
        }

        var payment = result.Payment!;
        return Ok(new RecurringPaymentReminderDto
        {
            Enabled = payment.PushReminderEnabled,
            Mode = payment.PushReminderMode,
            LeadDays = payment.PushReminderLeadDays
        });
    }

    // POST: api/recurring-payments/{id}/pay-early
    [HttpPost("{id}/pay-early")]
    public async Task<IActionResult> PostPayEarly(string id, PayEarlyRequestDto dto)
    {
        if (!DateOnly.TryParseExact(dto.OccurrenceDate, "yyyy-MM-dd", out var occurrenceDate))
        {
            return BadRequest(new { message = "occurrenceDate must use yyyy-MM-dd format." });
        }

        var result = await _payEarlyService.PayEarlyAsync(id, occurrenceDate, HttpContext.RequestAborted, dto.ClientKey);

        return result.Status switch
        {
            PayEarlyStatus.PaymentNotFound => NotFound(),
            PayEarlyStatus.PaymentInactive => BadRequest(new { message = result.Message }),
            PayEarlyStatus.AutomaticPayment => BadRequest(new { message = result.Message }),
            PayEarlyStatus.NoUpcomingOccurrence => BadRequest(new { message = result.Message }),
            PayEarlyStatus.Conflict => Conflict(new { message = result.Message }),
            _ => Ok(new
            {
                transaction = TransactionsController.MapToDto(result.Transaction!),
                settledOccurrenceDate = result.SettledOccurrenceDate!.Value.ToString("yyyy-MM-dd"),
                nextOccurrenceDate = result.NextOccurrenceDate?.ToString("yyyy-MM-dd")
            })
        };
    }

    [HttpPost("{id}/occurrences/{occurrenceDate}/settle")]
    public async Task<IActionResult> PostSettlement(
        string id,
        string occurrenceDate,
        RecurringOccurrenceSettlementDto dto)
    {
        if (_settlementService == null) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (!DateOnly.TryParseExact(occurrenceDate, "yyyy-MM-dd", out var parsedOccurrence))
        {
            return BadRequest(new { message = "occurrenceDate must use yyyy-MM-dd format." });
        }
        DateOnly? paidDate = null;
        if (!string.IsNullOrWhiteSpace(dto.PaidDate))
        {
            if (!DateOnly.TryParseExact(dto.PaidDate, "yyyy-MM-dd", out var parsedPaidDate))
            {
                return BadRequest(new { message = "paidDate must use yyyy-MM-dd format." });
            }
            paidDate = parsedPaidDate;
        }

        var result = await _settlementService.SettleAsync(
            id,
            parsedOccurrence,
            dto.Status,
            paidDate,
            dto.ClientKey,
            HttpContext.RequestAborted,
            dto.TransactionId,
            dto.PostedAt);
        return result.Status switch
        {
            RecurringSettlementStatus.NotFound => NotFound(),
            RecurringSettlementStatus.Invalid => BadRequest(new { message = result.Message }),
            RecurringSettlementStatus.AutomaticPayment => BadRequest(new { message = result.Message }),
            RecurringSettlementStatus.Conflict => Conflict(new { message = result.Message }),
            _ => Ok(new
            {
                occurrence = MapOccurrence(result.Occurrence!),
                transaction = result.Transaction == null ? null : TransactionsController.MapToDto(result.Transaction),
                nextOccurrenceDate = result.NextOccurrenceDate?.ToString("yyyy-MM-dd")
            })
        };
    }

    private static object MapOccurrence(RecurringPaymentOccurrence occurrence) => new
    {
        id = occurrence.Id,
        recurringPaymentId = occurrence.RecurringPaymentId,
        name = occurrence.Name,
        amount = occurrence.ScheduledAmount.HasValue
            ? ObfuscationHelper.Obfuscate(occurrence.ScheduledAmount.Value)
            : null,
        category = occurrence.Category,
        ledgerCategory = occurrence.LedgerCategory,
        dueDate = occurrence.OccurrenceDate.ToString("yyyy-MM-dd"),
        isPaid = occurrence.Status == RecurringOccurrenceStatus.Paid,
        isDiscarded = occurrence.Status == RecurringOccurrenceStatus.Discarded,
        status = occurrence.Status,
        paidDate = occurrence.PaidDate?.ToString("yyyy-MM-dd")
    };

    internal static RecurringPaymentDto MapToDto(RecurringPayment rp)
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
            EndDate = rp.EndDate,
            ReminderEnabled = rp.PushReminderEnabled,
            ReminderMode = rp.PushReminderMode,
            ReminderLeadDays = rp.PushReminderLeadDays,
            PaymentMode = rp.PaymentMode
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

    private static bool TryNormalizePaymentMode(string? value, out string paymentMode)
    {
        if (string.Equals(value, RecurringPaymentMode.AutoDeduct, StringComparison.OrdinalIgnoreCase))
        {
            paymentMode = RecurringPaymentMode.AutoDeduct;
            return true;
        }
        if (string.Equals(value, RecurringPaymentMode.Manual, StringComparison.OrdinalIgnoreCase))
        {
            paymentMode = RecurringPaymentMode.Manual;
            return true;
        }

        paymentMode = string.Empty;
        return false;
    }

    internal static RecurringPaymentDto MapToDto(RecurringPaymentProjection rp)
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
            EndDate = rp.EndDate,
            ReminderEnabled = rp.ReminderEnabled,
            ReminderMode = rp.ReminderMode,
            ReminderLeadDays = rp.ReminderLeadDays,
            PaymentMode = rp.PaymentMode
        };
    }
}

public class ToggleActiveDto
{
    public bool? Active { get; set; }
}

public class RecurringPaymentReminderDto
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "Once";
    public int LeadDays { get; set; } = 1;
}

public class PayEarlyRequestDto
{
    public string OccurrenceDate { get; set; } = string.Empty;
    public string? ClientKey { get; set; }
}

public class RecurringOccurrenceSettlementDto
{
    public string Status { get; set; } = string.Empty;
    public string? PaidDate { get; set; }
    public string? ClientKey { get; set; }
    public string? TransactionId { get; set; }
    public DateTime? PostedAt { get; set; }
}

public class RecurringPaymentDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Amount { get; set; } = string.Empty;
    public string Frequency { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string LedgerCategory { get; set; } = string.Empty;
    public string? NextDueDate { get; set; }
    public int DueDate { get; set; }
    public string StartDate { get; set; } = string.Empty;
    public bool Active { get; set; }
    public string? EndDate { get; set; }
    public bool ReminderEnabled { get; set; }
    public string ReminderMode { get; set; } = "Once";
    public int ReminderLeadDays { get; set; } = 1;
    public string PaymentMode { get; set; } = RecurringPaymentMode.Manual;
}
