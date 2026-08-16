using Microsoft.AspNetCore.Mvc;
using FinancialAppApi.Filters;
using FinancialAppApi.Services;
using FinancialAppApi.Database;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/financial")]
[AuthorizeToken]
public class FinancialController : ControllerBase
{
    private readonly FinancialService _financialService;

    public FinancialController(FinancialService financialService)
    {
        _financialService = financialService;
    }

    // GET: api/financial/wallet-balance
    [HttpGet("wallet-balance")]
    public async Task<ActionResult<object>> GetWalletBalance()
    {
        return Ok(await _financialService.GetWalletBalanceAsync(HttpContext.RequestAborted));
    }

    // GET: api/financial/dashboard
    [HttpGet("dashboard")]
    public async Task<ActionResult<object>> GetDashboardData(
        [FromQuery(Name = "month")] string? queryMonth = null,
        [FromQuery(Name = "year")] int? queryYear = null,
        [FromQuery(Name = "persistSelection")] bool persistSelection = true,
        [FromQuery(Name = "summaryOnly")] bool summaryOnly = false)
    {
        if (!IsValidPeriod(queryMonth, queryYear))
            return BadRequest(new { message = "Month must be a valid three-letter abbreviation and include a year." });
        return Ok(await _financialService.GetDashboardDataAsync(
            queryMonth,
            queryYear,
            persistSelection,
            summaryOnly,
            HttpContext.RequestAborted));
    }

    // GET: api/financial/dashboard/insights
    [HttpGet("dashboard/insights")]
    public async Task<ActionResult<object>> GetDashboardInsights(
        [FromQuery(Name = "month")] string? queryMonth = null,
        [FromQuery(Name = "year")] int? queryYear = null)
    {
        if (!IsValidPeriod(queryMonth, queryYear))
            return BadRequest(new { message = "Month must be a valid three-letter abbreviation and include a year." });
        return Ok(await _financialService.GetDashboardInsightsAsync(
            queryMonth,
            queryYear,
            HttpContext.RequestAborted));
    }

    // PUT: api/financial/settings
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateSettingsDto updateDto)
    {
        var validationError = await _financialService.UpdateSettingsAsync(new FinancialSettingsUpdate(
            updateDto.TargetStabilityFund,
            updateDto.EssentialsAlloc,
            updateDto.GrowthAlloc,
            updateDto.StabilityAlloc,
            updateDto.RewardsAlloc,
            updateDto.CycleDay,
            updateDto.DarkMode,
            updateDto.HideSensitive,
            updateDto.Currency,
            updateDto.StabilityOverflowRedirect),
            HttpContext.RequestAborted);
        if (validationError != null)
        {
            return BadRequest(new { message = validationError });
        }
        return NoContent();
    }

    // PUT: api/financial/dark-mode
    [HttpPut("dark-mode")]
    public async Task<IActionResult> UpdateDarkMode([FromBody] UpdateDarkModeDto dto)
    {
        await _financialService.UpdateDarkModeAsync(dto.DarkMode, HttpContext.RequestAborted);
        return NoContent();
    }

    // PUT: api/financial/hide-sensitive
    [HttpPut("hide-sensitive")]
    public async Task<IActionResult> UpdateHideSensitive([FromBody] UpdateHideSensitiveDto dto)
    {
        await _financialService.UpdateHideSensitiveAsync(dto.HideSensitive, HttpContext.RequestAborted);
        return NoContent();
    }

    // PUT: api/financial/summary-seen
    [HttpPut("summary-seen")]
    public async Task<IActionResult> UpdateSummarySeen([FromBody] UpdateSummarySeenDto dto)
    {
        if (!string.IsNullOrWhiteSpace(dto.CycleKey) &&
            !System.Text.RegularExpressions.Regex.IsMatch(dto.CycleKey, "^[0-9]{4}-[0-9]{2}$"))
        {
            return BadRequest(new { message = "Cycle key must be in yyyy-MM format." });
        }
        await _financialService.UpdateSummarySeenAsync(dto.CycleKey, HttpContext.RequestAborted);
        return NoContent();
    }

    // POST: api/financial/select-period
    [HttpPost("select-period")]
    public async Task<IActionResult> SelectPeriod([FromBody] SelectPeriodDto periodDto)
    {
        if (!IsValidPeriod(periodDto.SelectedMonth, periodDto.SelectedYear))
            return BadRequest(new { message = "Month must be a valid three-letter abbreviation and include a year." });
        await _financialService.SelectPeriodAsync(
            periodDto.SelectedMonth,
            periodDto.SelectedYear,
            HttpContext.RequestAborted);
        return NoContent();
    }

    private static bool IsValidPeriod(string? month, int? year)
    {
        if (month == null && year == null) return true;
        return year.HasValue && year.Value > 0 && month != null &&
            FinancialConstants.MonthAbbreviations.Contains(month, StringComparer.Ordinal);
    }
}

public class UpdateSettingsDto
{
    public string TargetStabilityFund { get; set; } = string.Empty;
    public decimal EssentialsAlloc { get; set; }
    public decimal GrowthAlloc { get; set; }
    public decimal StabilityAlloc { get; set; }
    public decimal RewardsAlloc { get; set; }
    public int CycleDay { get; set; }
    public bool? DarkMode { get; set; }
    public bool? HideSensitive { get; set; }
    public string Currency { get; set; } = "USD";
    [System.Text.Json.Serialization.JsonPropertyName("stabilityOverflowRedirect")]
    public string? StabilityOverflowRedirect { get; set; }
}

public class UpdateDarkModeDto
{
    public bool DarkMode { get; set; }
}

public class UpdateHideSensitiveDto
{
    public bool HideSensitive { get; set; }
}

public class UpdateSummarySeenDto
{
    public string? CycleKey { get; set; }
}

public class SelectPeriodDto
{
    public string SelectedMonth { get; set; } = string.Empty;
    public int SelectedYear { get; set; }
}
