using System.Globalization;
using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Filters;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Loans;
using Microsoft.AspNetCore.Mvc;
using FinancialAppApi.Contracts;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/loans")]
[AuthorizeToken]
[RefreshSlices(RefreshSliceNames.Loans, RefreshSliceNames.Recurring)]
public sealed class LoansController : ControllerBase
{
    private const int BootstrapSchedulePreviewLength = 6;
    private readonly LoanService _loanService;
    private readonly LoanRepaymentService _repaymentService;

    public LoansController(LoanService loanService, LoanRepaymentService repaymentService)
    {
        _loanService = loanService;
        _repaymentService = repaymentService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<LoanDto>>> GetLoans()
    {
        var loans = await _loanService.GetLoansAsync(HttpContext.RequestAborted);
        return Ok(loans.Select(MapToDto).ToList());
    }

    [HttpGet("{id}/schedule")]
    public async Task<ActionResult<IEnumerable<LoanScheduleDto>>> GetLoanSchedule(string id)
    {
        var loan = await _loanService.GetLoanAsync(id, HttpContext.RequestAborted);
        return loan == null
            ? NotFound()
            : Ok(loan.Replay.FutureSchedule.Select(MapToDto).ToList());
    }

    [HttpPost("{id}/repayments/preview")]
    [NoFinancialRefresh]
    public async Task<IActionResult> PreviewRepayment(string id, [FromBody] AdvanceRepaymentPreviewRequestDto? dto = null)
    {
        var cycles = dto?.Cycles ?? 1;
        var result = await _repaymentService.PreviewAdvanceRepaymentAsync(id, cycles, HttpContext.RequestAborted);
        return result.Status switch
        {
            LoanRepaymentStatus.NotFound => NotFound(),
            LoanRepaymentStatus.Invalid => BadRequest(new { message = result.Message }),
            _ => Ok(new
            {
                cyclesCount = result.CyclesCount,
                totalAmount = ObfuscationHelper.Obfuscate(result.TotalAmount),
                previewFingerprint = result.PreviewFingerprint,
                occurrences = result.Occurrences?.Select(MapToDto).ToList() ?? []
            })
        };
    }

    [HttpPost("{id}/repayments/advance-cycles")]
    [RefreshSlices(RefreshSliceNames.Core, RefreshSliceNames.Loans, RefreshSliceNames.Recurring)]
    public async Task<IActionResult> AdvanceCyclesRepayment(string id, [FromBody] AdvanceCyclesRepaymentDto dto)
    {
        var result = await _repaymentService.AdvanceCyclesRepaymentAsync(
            id,
            dto.Cycles,
            dto.AccountId,
            dto.ClientKey,
            dto.PreviewFingerprint,
            dto.PostedAt,
            HttpContext.RequestAborted);

        return result.Status switch
        {
            LoanRepaymentStatus.NotFound => NotFound(),
            LoanRepaymentStatus.Conflict => Conflict(new { code = result.Code, message = result.Message }),
            LoanRepaymentStatus.Invalid => BadRequest(new { code = result.Code, message = result.Message, missingBuckets = result.MissingBuckets }),
            _ => Ok(new
            {
                actionId = result.ActionId,
                kind = result.Kind,
                loan = MapToDto(result.LoanView!),
                transactions = result.Transactions?.Select(t => TransactionsController.MapToDto(t)).ToList() ?? []
            })
        };
    }

    [HttpPost("{id}/repayments/full-settlement")]
    [RefreshSlices(RefreshSliceNames.Core, RefreshSliceNames.Loans, RefreshSliceNames.Recurring)]
    public async Task<IActionResult> FullSettlementRepayment(string id, [FromBody] FullSettlementRepaymentDto dto)
    {
        if (!ObfuscationHelper.TryDeobfuscate(dto.LenderQuoteAmount, out var quote) || quote < 0.01m)
        {
            return BadRequest(new { message = "Lender quote amount must be a positive number of at least 0.01." });
        }

        var result = await _repaymentService.FullSettlementRepaymentAsync(
            id,
            quote,
            dto.AccountId,
            dto.ClientKey,
            dto.PostedAt,
            HttpContext.RequestAborted);

        return result.Status switch
        {
            LoanRepaymentStatus.NotFound => NotFound(),
            LoanRepaymentStatus.Conflict => Conflict(new { message = result.Message }),
            LoanRepaymentStatus.Invalid => BadRequest(new { code = result.Code, message = result.Message, missingBuckets = result.MissingBuckets }),
            _ => Ok(new
            {
                actionId = result.ActionId,
                kind = result.Kind,
                loan = MapToDto(result.LoanView!),
                transactions = result.Transactions?.Select(t => TransactionsController.MapToDto(t)).ToList() ?? []
            })
        };
    }

    [HttpPost("repayments/{actionId}/undo")]
    [RefreshSlices(RefreshSliceNames.Core, RefreshSliceNames.Loans, RefreshSliceNames.Recurring)]
    public async Task<IActionResult> UndoRepayment(string actionId)
    {
        var result = await _repaymentService.UndoRepaymentActionAsync(actionId, HttpContext.RequestAborted);
        return result.Status switch
        {
            LoanRepaymentStatus.NotFound => NotFound(),
            LoanRepaymentStatus.Conflict => Conflict(new { message = result.Message }),
            LoanRepaymentStatus.Invalid => BadRequest(new { message = result.Message }),
            _ => Ok(new
            {
                actionId = result.ActionId,
                kind = result.Kind,
                loan = result.LoanView == null ? null : MapToDto(result.LoanView)
            })
        };
    }

    [HttpPost]
    [RefreshSlices(RefreshSliceNames.Loans, RefreshSliceNames.Recurring)]
    public async Task<ActionResult<LoanDto>> PostLoan(LoanMutationDto dto)
    {
        var result = await _loanService.CreateLoanAsync(ToLoan(dto), HttpContext.RequestAborted);
        return result.Status switch
        {
            LoanMutationStatus.Success => Ok(MapToDto(result.View!)),
            LoanMutationStatus.RecurringPaymentAlreadyLinked => Conflict(new { message = result.Message }),
            LoanMutationStatus.Conflict => Conflict(new { message = result.Message }),
            _ => BadRequest(new { message = result.Message })
        };
    }

    [HttpPut("{id}")]
    [RefreshSlices(RefreshSliceNames.Loans, RefreshSliceNames.Recurring)]
    public async Task<IActionResult> PutLoan(string id, LoanMutationDto dto)
    {
        var result = await _loanService.UpdateLoanAsync(id, ToLoan(dto), HttpContext.RequestAborted);
        return result.Status switch
        {
            LoanMutationStatus.NotFound => NotFound(),
            LoanMutationStatus.RecurringPaymentAlreadyLinked => Conflict(new { message = result.Message }),
            LoanMutationStatus.Conflict => Conflict(new { message = result.Message }),
            LoanMutationStatus.Success => Ok(MapToDto(result.View!)),
            _ => BadRequest(new { message = result.Message })
        };
    }

    [HttpDelete("{id}")]
    [RefreshSlices(RefreshSliceNames.Loans, RefreshSliceNames.Recurring)]
    public async Task<IActionResult> DeleteLoan(string id)
    {
        var status = await _loanService.DeleteLoanAsync(id, HttpContext.RequestAborted);
        return status == LoanMutationStatus.NotFound ? NotFound() : NoContent();
    }

    internal static LoanDto MapToDto(LoanView view)
    {
        var replay = view.Replay;
        var next = replay.FutureSchedule.FirstOrDefault();
        return new LoanDto
        {
            Id = view.Loan.Id,
            Name = view.Loan.Name,
            RecurringPaymentId = view.Loan.RecurringPaymentId,
            OpeningPrincipal = ObfuscationHelper.Obfuscate(view.Loan.OpeningPrincipal),
            TrackingStartDate = view.Loan.TrackingStartDate.ToString("yyyy-MM-dd"),
            AnnualRatePercent = view.Loan.AnnualRatePercent,
            RateBasis = view.Loan.RateBasis,
            TermPeriods = view.Loan.TermPeriods,
            InterestMethod = view.Loan.InterestMethod,
            RecurringPaymentExists = view.RecurringPayment != null,
            RecurringPaymentName = view.RecurringPayment?.Name,
            RecurringPaymentFrequency = view.RecurringPayment?.Frequency,
            RecurringPaymentDueDate = view.RecurringPayment?.DueDate,
            RecurringPaymentLedgerCategory = view.RecurringPayment?.LedgerCategory,
            ScheduleFrequency = view.Loan.ScheduleFrequency,
            ScheduleDueDay = view.Loan.ScheduleDueDay,
            ScheduleStartDate = view.Loan.ScheduleStartDate?.ToString("yyyy-MM-dd"),
            ScheduleStatus = view.Loan.ScheduleStatus,
            SettlementActionId = view.SettlementActionId,
            Snapshot = new LoanSnapshotDto
            {
                OutstandingBalance = ObfuscationHelper.Obfuscate(replay.OutstandingBalance),
                ScheduledPayment = ObfuscationHelper.Obfuscate(replay.ScheduledPayment),
                TotalScheduledInterest = ObfuscationHelper.Obfuscate(replay.TotalScheduledInterest),
                TotalInterestPaid = ObfuscationHelper.Obfuscate(replay.TotalInterestPaid),
                PayoffDate = replay.PayoffDate?.ToString("yyyy-MM-dd"),
                LastOccurrenceDate = replay.LastOccurrenceDate?.ToString("yyyy-MM-dd"),
                NextPayment = next == null ? null : MapToDto(next),
                Payments = replay.Payments.Select(MapToDto).ToList(),
                FutureSchedule = replay.FutureSchedule
                    .Take(BootstrapSchedulePreviewLength)
                    .Select(MapToDto)
                    .ToList()
            }
        };
    }

    private static LoanPaymentDto MapToDto(LoanPaymentSplit payment) => new()
    {
        OccurrenceDate = payment.OccurrenceDate.ToString("yyyy-MM-dd"),
        Payment = ObfuscationHelper.Obfuscate(payment.Payment),
        Interest = ObfuscationHelper.Obfuscate(payment.Interest),
        Principal = ObfuscationHelper.Obfuscate(payment.Principal),
        BalanceBefore = ObfuscationHelper.Obfuscate(payment.BalanceBefore),
        BalanceAfter = ObfuscationHelper.Obfuscate(payment.BalanceAfter),
        Surplus = ObfuscationHelper.Obfuscate(payment.Surplus),
        PaymentDidNotCoverInterest = payment.PaymentDidNotCoverInterest,
        TransactionId = payment.TransactionId
    };

    private static LoanScheduleDto MapToDto(LoanScheduleEntry entry) => new()
    {
        OccurrenceDate = entry.OccurrenceDate.ToString("yyyy-MM-dd"),
        Payment = ObfuscationHelper.Obfuscate(entry.Payment),
        Interest = ObfuscationHelper.Obfuscate(entry.Interest),
        Principal = ObfuscationHelper.Obfuscate(entry.Principal),
        BalanceAfter = ObfuscationHelper.Obfuscate(entry.BalanceAfter)
    };

    private static Loan ToLoan(LoanMutationDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name?.Trim() ?? string.Empty,
        RecurringPaymentId = dto.RecurringPaymentId ?? string.Empty,
        OpeningPrincipal = Math.Round(ReadWireAmount(dto.OpeningPrincipal), 2, MidpointRounding.AwayFromZero),
        TrackingStartDate = ParseDate(dto.TrackingStartDate),
        AnnualRatePercent = Math.Round(ReadWireRate(dto.AnnualRatePercent), 4, MidpointRounding.AwayFromZero),
        RateBasis = dto.RateBasis ?? LoanRateBasis.Yearly,
        TermPeriods = dto.TermPeriods,
        InterestMethod = dto.InterestMethod ?? LoanInterestMethod.ReducingBalance
    };

    private static DateOnly ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", out var parsed) ? parsed : default;

    private static decimal ReadWireAmount(JsonElement amount) => amount.ValueKind switch
    {
        JsonValueKind.String => ObfuscationHelper.Deobfuscate(amount.GetString() ?? string.Empty),
        JsonValueKind.Number when amount.TryGetDecimal(out var value) => value,
        _ => 0m
    };

    private static decimal ReadWireRate(JsonElement rate) => rate.ValueKind switch
    {
        JsonValueKind.Number when rate.TryGetDecimal(out var value) => value,
        JsonValueKind.String when decimal.TryParse(
            rate.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
        JsonValueKind.String => ObfuscationHelper.Deobfuscate(rate.GetString() ?? string.Empty),
        _ => 0m
    };
}

public sealed class LoanMutationDto
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? RecurringPaymentId { get; set; }
    public JsonElement OpeningPrincipal { get; set; }
    public string? TrackingStartDate { get; set; }
    public JsonElement AnnualRatePercent { get; set; }
    public string? RateBasis { get; set; }
    public int TermPeriods { get; set; }
    public string? InterestMethod { get; set; }
}

public sealed class LoanDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string RecurringPaymentId { get; set; } = string.Empty;
    public string OpeningPrincipal { get; set; } = string.Empty;
    public string TrackingStartDate { get; set; } = string.Empty;
    public decimal AnnualRatePercent { get; set; }
    public string RateBasis { get; set; } = LoanRateBasis.Yearly;
    public int TermPeriods { get; set; }
    public string InterestMethod { get; set; } = LoanInterestMethod.ReducingBalance;
    public bool RecurringPaymentExists { get; set; }
    public string? RecurringPaymentName { get; set; }
    public string? RecurringPaymentFrequency { get; set; }
    public int? RecurringPaymentDueDate { get; set; }
    public string? RecurringPaymentLedgerCategory { get; set; }
    public string? ScheduleFrequency { get; set; }
    public int? ScheduleDueDay { get; set; }
    public string? ScheduleStartDate { get; set; }
    public string ScheduleStatus { get; set; } = LoanScheduleStatus.Incomplete;
    public string? SettlementActionId { get; set; }
    public LoanSnapshotDto Snapshot { get; set; } = new();
}

public sealed class LoanSnapshotDto
{
    public string OutstandingBalance { get; set; } = string.Empty;
    public string ScheduledPayment { get; set; } = string.Empty;
    public string TotalScheduledInterest { get; set; } = string.Empty;
    public string TotalInterestPaid { get; set; } = string.Empty;
    public string? PayoffDate { get; set; }
    public string? LastOccurrenceDate { get; set; }
    public LoanScheduleDto? NextPayment { get; set; }
    public List<LoanPaymentDto> Payments { get; set; } = [];
    public List<LoanScheduleDto> FutureSchedule { get; set; } = [];
}

public sealed class LoanPaymentDto
{
    public string OccurrenceDate { get; set; } = string.Empty;
    public string Payment { get; set; } = string.Empty;
    public string Interest { get; set; } = string.Empty;
    public string Principal { get; set; } = string.Empty;
    public string BalanceBefore { get; set; } = string.Empty;
    public string BalanceAfter { get; set; } = string.Empty;
    public string Surplus { get; set; } = string.Empty;
    public bool PaymentDidNotCoverInterest { get; set; }
    public string? TransactionId { get; set; }
}

public sealed class LoanScheduleDto
{
    public string OccurrenceDate { get; set; } = string.Empty;
    public string Payment { get; set; } = string.Empty;
    public string Interest { get; set; } = string.Empty;
    public string Principal { get; set; } = string.Empty;
    public string BalanceAfter { get; set; } = string.Empty;
}

public sealed class AdvanceRepaymentPreviewRequestDto
{
    public int Cycles { get; set; } = 1;
}

public sealed class AdvanceCyclesRepaymentDto
{
    public int Cycles { get; set; } = 1;
    public string? AccountId { get; set; }
    public string? ClientKey { get; set; }
    public string? PreviewFingerprint { get; set; }
    public DateTime? PostedAt { get; set; }
}

public sealed class FullSettlementRepaymentDto
{
    public string LenderQuoteAmount { get; set; } = string.Empty;
    public string? AccountId { get; set; }
    public string? ClientKey { get; set; }
    public DateTime? PostedAt { get; set; }
}
