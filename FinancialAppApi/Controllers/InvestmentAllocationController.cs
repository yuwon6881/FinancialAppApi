using FinancialAppApi.Database;
using FinancialAppApi.Filters;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Controllers;

[ApiController]
[AuthorizeToken]
[Route("api/investments")]
public sealed class InvestmentAllocationController(
    AppDbContext context,
    InvestmentPortfolioService portfolioService) : ControllerBase
{
    [HttpGet("allocation")]
    public async Task<ActionResult<InvestmentAllocationOverviewDto>> GetAllocation()
        => Ok((await portfolioService.GetPortfolioAsync("1m", HttpContext.RequestAborted)).Allocation);

    [HttpPut("allocation/plan")]
    public async Task<ActionResult<InvestmentPlanDto>> UpdatePlan(InvestmentPlanMutationDto dto)
    {
        var error = InvestmentAllocationService.ValidatePlan(dto);
        if (error is not null) return BadRequest(new { message = error });

        var plan = await context.InvestmentPlans.SingleOrDefaultAsync(HttpContext.RequestAborted);
        if (plan is null)
        {
            plan = new InvestmentPlan();
            context.InvestmentPlans.Add(plan);
        }
        plan.UsEquityTarget = dto.UsEquityTarget;
        plan.InternationalExUsTarget = dto.InternationalExUsTarget;
        plan.BondsTarget = dto.BondsTarget;
        plan.WatchDrift = dto.WatchDrift;
        plan.AlertDrift = dto.AlertDrift;
        plan.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(InvestmentAllocationService.ToDto(plan));
    }

    [HttpPut("instruments/{id:guid}/allocation-sleeve")]
    public async Task<IActionResult> UpdateAllocationSleeve(Guid id, AllocationSleeveMutationDto dto)
    {
        if (dto.Sleeve is not null && !InvestmentKinds.AllocationSleeves.Contains(dto.Sleeve))
            return BadRequest(new { message = "Sleeve must be US Equity, International ex-US, Bonds, or unassigned." });
        var instrument = await context.InvestmentInstruments.FindAsync([id], HttpContext.RequestAborted);
        if (instrument is null) return NotFound();
        instrument.AllocationSleeve = dto.Sleeve is null
            ? null
            : InvestmentKinds.AllocationSleeves.Single(value =>
                value.Equals(dto.Sleeve, StringComparison.OrdinalIgnoreCase));
        instrument.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpPut("allocation/order")]
    public async Task<IActionResult> UpdateAllocationOrder(AllocationOrderMutationDto dto)
    {
        if (dto.InstrumentIds.Count != dto.InstrumentIds.Distinct().Count())
            return BadRequest(new { message = "Each investment may appear only once in the classification order." });

        var instruments = await context.InvestmentInstruments
            .Where(value => dto.InstrumentIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, HttpContext.RequestAborted);
        if (instruments.Count != dto.InstrumentIds.Count)
            return BadRequest(new { message = "The classification order contains an unknown investment." });

        for (var index = 0; index < dto.InstrumentIds.Count; index++)
        {
            var instrument = instruments[dto.InstrumentIds[index]];
            instrument.AllocationOrder = index;
            instrument.UpdatedAt = DateTime.UtcNow;
        }
        await context.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }
}

public sealed record AllocationOrderMutationDto(IReadOnlyList<Guid> InstrumentIds);
