using FinancialAppApi.Filters;
using FinancialAppApi.Services.Forecast;
using Microsoft.AspNetCore.Mvc;

namespace FinancialAppApi.Controllers;

public sealed class CashFlowScenarioItemDto
{
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    /// <summary>Essentials, Growth, Stability or Rewards; null for income split like a salary.</summary>
    public string? Bucket { get; set; }
    public string Label { get; set; } = string.Empty;
}

public sealed class CashFlowScenarioDto
{
    public int Cycles { get; set; } = 4;
    public decimal? IncomePerCycle { get; set; }
    public decimal? EverydaySpendingPerCycle { get; set; }
    public List<CashFlowScenarioItemDto>? Items { get; set; }
    public List<string>? SkippedBills { get; set; }
}

/// <summary>
/// The forecast workspace. POST carries a temporary scenario and only reads: it writes nothing
/// and so asks the client to refresh nothing.
/// </summary>
[ApiController]
[AuthorizeToken]
[Route("api/forecast")]
[NoFinancialRefresh]
public sealed class ForecastController(CashFlowForecastService forecastService) : ControllerBase
{
    [HttpPost("cash-flow")]
    public async Task<ActionResult<CashFlowForecastResponse>> CashFlow(CashFlowScenarioDto? dto)
    {
        var scenario = new CashFlowScenario(
            dto?.Cycles ?? 4,
            dto?.IncomePerCycle,
            dto?.EverydaySpendingPerCycle,
            (dto?.Items ?? []).Select(item => new CashFlowScenarioItem(item.Date, item.Amount, item.Bucket, item.Label ?? string.Empty)).ToList(),
            dto?.SkippedBills ?? []);
        var error = CashFlowForecastService.Validate(scenario);
        if (error is not null) return BadRequest(new { message = error });

        return Ok(await forecastService.ForecastAsync(scenario, HttpContext.RequestAborted));
    }
}
