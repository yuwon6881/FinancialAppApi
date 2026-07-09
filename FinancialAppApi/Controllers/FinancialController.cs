using Microsoft.AspNetCore.Mvc;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;
using FinancialAppApi.Services;

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
        return Ok(await _financialService.GetWalletBalanceAsync());
    }

    // GET: api/financial/dashboard
    [HttpGet("dashboard")]
    public async Task<ActionResult<object>> GetDashboardData(
        [FromQuery(Name = "month")] string? queryMonth = null,
        [FromQuery(Name = "year")] int? queryYear = null)
    {
        return Ok(await _financialService.GetDashboardDataAsync(queryMonth, queryYear));
    }

    // GET: api/financial/dashboard/insights
    [HttpGet("dashboard/insights")]
    public async Task<ActionResult<object>> GetDashboardInsights(
        [FromQuery(Name = "month")] string? queryMonth = null,
        [FromQuery(Name = "year")] int? queryYear = null)
    {
        return Ok(await _financialService.GetDashboardInsightsAsync(queryMonth, queryYear));
    }

    // PUT: api/financial/settings
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateSettingsDto updateDto)
    {
        await _financialService.UpdateSettingsAsync(new FinancialSettingsUpdate(
            updateDto.TargetStabilityFund,
            updateDto.EssentialsAlloc,
            updateDto.GrowthAlloc,
            updateDto.StabilityAlloc,
            updateDto.RewardsAlloc,
            updateDto.CycleDay,
            updateDto.DarkMode,
            updateDto.HideSensitive,
            updateDto.VibrationEnabled,
            updateDto.Currency,
            updateDto.StabilityOverflowRedirect));
        return NoContent();
    }

    // PUT: api/financial/dark-mode
    [HttpPut("dark-mode")]
    public async Task<IActionResult> UpdateDarkMode([FromBody] UpdateDarkModeDto dto)
    {
        await _financialService.UpdateDarkModeAsync(dto.DarkMode);
        return NoContent();
    }

    // PUT: api/financial/hide-sensitive
    [HttpPut("hide-sensitive")]
    public async Task<IActionResult> UpdateHideSensitive([FromBody] UpdateHideSensitiveDto dto)
    {
        await _financialService.UpdateHideSensitiveAsync(dto.HideSensitive);
        return NoContent();
    }

    // PUT: api/financial/vibration
    [HttpPut("vibration")]
    public async Task<IActionResult> UpdateVibration([FromBody] UpdateVibrationDto dto)
    {
        await _financialService.UpdateVibrationAsync(dto.VibrationEnabled);
        return NoContent();
    }

    // POST: api/financial/select-period
    [HttpPost("select-period")]
    public async Task<IActionResult> SelectPeriod([FromBody] FinancialSetting periodDto)
    {
        await _financialService.SelectPeriodAsync(periodDto.SelectedMonth, periodDto.SelectedYear);
        return NoContent();
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
    public bool? VibrationEnabled { get; set; }
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

public class UpdateVibrationDto
{
    public bool VibrationEnabled { get; set; }
}
