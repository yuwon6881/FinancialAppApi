using FinancialAppApi.Services.Investments;
using Microsoft.AspNetCore.Mvc;

namespace FinancialAppApi.Controllers;

public sealed partial class InvestmentsController
{
    /// <summary>
    /// Settled month-end history: months, calendar years, and the lifetime, overall and per
    /// market. Served from the snapshot cache, so the cost tracks months changed, not account age.
    /// </summary>
    [HttpGet("history")]
    public async Task<ActionResult<InvestmentHistoryDto>> GetHistory()
        => Ok(await snapshotService.GetHistoryAsync(HttpContext.RequestAborted));
}
