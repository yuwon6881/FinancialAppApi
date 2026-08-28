using FinancialAppApi.Filters;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;
using Microsoft.AspNetCore.Mvc;
using FinancialAppApi.Contracts;

namespace FinancialAppApi.Controllers;

[ApiController]
[AuthorizeToken]
[Route("api/investments")]
[RefreshSlices(RefreshSliceNames.Investments)]
public sealed class InvestmentAllocationController(
    InvestmentPortfolioService portfolioService,
    InvestmentAllocationService allocationService) : ControllerBase
{
    [HttpGet("allocation")]
    public async Task<ActionResult<InvestmentAllocationOverviewDto>> GetAllocation()
        => Ok(await portfolioService.GetAllocationAsync(HttpContext.RequestAborted));

    [HttpPut("allocation/plan")]
    public async Task<ActionResult<InvestmentPlanDto>> UpdatePlan(InvestmentPlanMutationDto dto)
    {
        var error = InvestmentAllocationService.ValidatePlan(dto);
        if (error is not null) return BadRequest(new { message = error });

        return Ok(await allocationService.UpdatePlanAsync(dto, HttpContext.RequestAborted));
    }

    [HttpPut("instruments/{id:guid}/allocation-sleeve")]
    public async Task<IActionResult> UpdateAllocationSleeve(Guid id, AllocationSleeveMutationDto dto)
    {
        if (dto.Sleeve is not null && !InvestmentKinds.AllocationSleeves.Contains(dto.Sleeve))
            return BadRequest(new { message = "Sleeve must be US Equity, International ex-US, Bonds, or unassigned." });
        return await allocationService.UpdateAllocationSleeveAsync(id, dto.Sleeve, HttpContext.RequestAborted)
            ? NoContent()
            : NotFound();
    }

    [HttpPut("allocation/order")]
    public async Task<IActionResult> UpdateAllocationOrder(AllocationOrderMutationDto dto)
    {
        if (dto.InstrumentIds.Count != dto.InstrumentIds.Distinct().Count())
            return BadRequest(new { message = "Each investment may appear only once in the classification order." });

        if (!await allocationService.UpdateAllocationOrderAsync(dto.InstrumentIds, HttpContext.RequestAborted))
            return BadRequest(new { message = "The classification order contains an unknown investment." });
        return NoContent();
    }
}

public sealed record AllocationOrderMutationDto(IReadOnlyList<Guid> InstrumentIds);
