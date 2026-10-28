using System.Text.Json;
using FinancialAppApi.Services.SavingsGoals;
using Microsoft.AspNetCore.Mvc;

namespace FinancialAppApi.Controllers;

public partial class SavingsGoalsController
{
    // POST: api/savings-goals/{id}/interest
    // Records growth from pocket interest: increases the earmark and the bucket balance together.
    [HttpPost("{id}/interest")]
    public async Task<IActionResult> RecordInterest(int id, [FromBody] SavingsGoalInterestDto dto)
    {
        var amount = Math.Round(ReadWireAmount(dto.Amount), 2, MidpointRounding.AwayFromZero);
        var result = await _savingsGoalService.RecordInterestAsync(
            id,
            amount,
            dto.AccountId,
            dto.OperationId,
            HttpContext.RequestAborted);
        return result.Status switch
        {
            SavingsGoalMutationStatus.NotFound => NotFound(),
            SavingsGoalMutationStatus.Success => Ok(MapToDto(result.Goal!)),
            SavingsGoalMutationStatus.Conflict => Conflict(new { message = result.Message }),
            _ => BadRequest(new { message = result.Message })
        };
    }
}

public class SavingsGoalInterestDto
{
    public JsonElement Amount { get; set; }
    public string? AccountId { get; set; }
    public string? OperationId { get; set; }
}
