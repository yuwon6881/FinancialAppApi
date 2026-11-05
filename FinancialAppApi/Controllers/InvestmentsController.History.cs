using FinancialAppApi.Services.Investments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Controllers;

public sealed partial class InvestmentsController
{
    /// <summary>
    /// Settled month-end history: months, calendar years, and the lifetime, overall and per
    /// market. Served from the snapshot cache, so the cost tracks months changed, not account age.
    /// </summary>
    [HttpGet("history")]
    public async Task<ActionResult<InvestmentHistoryDto>> GetHistory()
    {
        var snapshots = await snapshotService.EnsureAsync(HttpContext.RequestAborted);
        var currency = await context.FinancialSettings.AsNoTracking()
            .Select(value => value.Currency)
            .FirstOrDefaultAsync(HttpContext.RequestAborted) ?? "USD";
        return Ok(InvestmentHistorySummarizer.Summarize(currency, snapshots));
    }
}
