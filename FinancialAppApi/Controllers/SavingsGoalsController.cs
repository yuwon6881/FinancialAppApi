using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Filters;
using FinancialAppApi.Models;
using FinancialAppApi.Services.SavingsGoals;
using Microsoft.AspNetCore.Mvc;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/savings-goals")]
[AuthorizeToken]
public class SavingsGoalsController : ControllerBase
{
    private readonly SavingsGoalService _savingsGoalService;

    public SavingsGoalsController(SavingsGoalService savingsGoalService)
    {
        _savingsGoalService = savingsGoalService;
    }

    // GET: api/savings-goals
    [HttpGet]
    public async Task<ActionResult<IEnumerable<SavingsGoalDto>>> GetGoals()
    {
        var goals = await _savingsGoalService.GetGoalsAsync(HttpContext.RequestAborted);
        return Ok(goals.Select(MapToDto).ToList());
    }

    // GET: api/savings-goals/pool
    // The one-balance view: what the Rewards pool holds, what goals have claimed, what is free.
    [HttpGet("pool")]
    public async Task<IActionResult> GetPool()
    {
        var summary = await _savingsGoalService.GetPoolSummaryAsync(HttpContext.RequestAborted);
        return Ok(MapToDto(summary));
    }

    // POST: api/savings-goals
    [HttpPost]
    public async Task<ActionResult<SavingsGoalDto>> PostGoal(SavingsGoalMutationDto dto)
    {
        var result = await _savingsGoalService.CreateGoalAsync(ToGoal(dto), HttpContext.RequestAborted);
        return result.Status switch
        {
            SavingsGoalMutationStatus.Success => CreatedAtAction(nameof(GetGoals), new { id = result.Goal!.Id }, MapToDto(result.Goal)),
            _ => BadRequest(new { message = result.Message })
        };
    }

    // PUT: api/savings-goals/{id}
    [HttpPut("{id}")]
    public async Task<IActionResult> PutGoal(int id, SavingsGoalMutationDto dto)
    {
        var result = await _savingsGoalService.UpdateGoalAsync(id, ToGoal(dto), HttpContext.RequestAborted);
        return result.Status switch
        {
            SavingsGoalMutationStatus.NotFound => NotFound(),
            SavingsGoalMutationStatus.Success => NoContent(),
            _ => BadRequest(new { message = result.Message })
        };
    }

    // DELETE: api/savings-goals/{id}
    // Releases the earmark; the money returns to the free-to-spend remainder.
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteGoal(int id)
    {
        var status = await _savingsGoalService.DeleteGoalAsync(id, HttpContext.RequestAborted);
        return status == SavingsGoalMutationStatus.NotFound ? NotFound() : NoContent();
    }

    // POST: api/savings-goals/{id}/contribute
    // Manual top-up (positive amount) or release (negative amount).
    [HttpPost("{id}/contribute")]
    public async Task<IActionResult> Contribute(int id, [FromBody] SavingsGoalContributionDto dto)
    {
        var amount = Math.Round(ReadWireAmount(dto.Amount), 2, MidpointRounding.AwayFromZero);
        var result = await _savingsGoalService.ContributeAsync(id, amount, HttpContext.RequestAborted);
        return result.Status switch
        {
            SavingsGoalMutationStatus.NotFound => NotFound(),
            SavingsGoalMutationStatus.Success => Ok(MapToDto(result.Goal!)),
            _ => BadRequest(new { message = result.Message })
        };
    }

    // POST: api/savings-goals/fund
    // Distributes the unassigned Rewards money across goals at their deadline-derived pace.
    // Idempotent within a cycle.
    [HttpPost("fund")]
    public async Task<IActionResult> FundCurrentCycle()
    {
        var result = await _savingsGoalService.FundCurrentCycleAsync(HttpContext.RequestAborted);
        return Ok(new
        {
            goals = result.Goals.Select(MapToDto).ToList(),
            totalGranted = ObfuscationHelper.Obfuscate(result.TotalGranted),
            freeToSpend = ObfuscationHelper.Obfuscate(result.FreeToSpend)
        });
    }

    // POST: api/savings-goals/{id}/complete
    [HttpPost("{id}/complete")]
    public async Task<IActionResult> CompleteGoal(int id)
    {
        var result = await _savingsGoalService.CompleteGoalAsync(id, HttpContext.RequestAborted);
        return result.Status switch
        {
            SavingsGoalMutationStatus.NotFound => NotFound(),
            SavingsGoalMutationStatus.Success => Ok(MapToDto(result.Goal!)),
            _ => BadRequest(new { message = result.Message })
        };
    }

    private static SavingsGoal ToGoal(SavingsGoalMutationDto dto)
    {
        return new SavingsGoal
        {
            Id = dto.Id,
            Name = dto.Name,
            TargetAmount = Math.Round(ReadWireAmount(dto.TargetAmount), 2, MidpointRounding.AwayFromZero),
            EarmarkedAmount = Math.Round(ReadWireAmount(dto.EarmarkedAmount), 2, MidpointRounding.AwayFromZero),
            TargetDate = ResolveTargetDate(dto.TargetDate),
            Priority = dto.Priority,
            IsRecurring = dto.IsRecurring,
            RecurrenceMonths = dto.RecurrenceMonths,
            CreatedAt = dto.CreatedAt == default ? DateTime.UtcNow : dto.CreatedAt,
            ClientKey = dto.ClientKey
        };
    }

    // The client sends 'YYYY-MM-DD'. Route it through the same parser the ledger uses so a goal
    // deadline and a transaction date cannot disagree about what day a string means.
    private static DateTime ResolveTargetDate(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && TransactionDate.TryParseInputDate(value, out var parsed))
        {
            return TransactionDate.FromInputDate(parsed);
        }
        return default;
    }

    internal static SavingsGoalDto MapToDto(SavingsGoal goal)
    {
        return new SavingsGoalDto
        {
            Id = goal.Id,
            Name = goal.Name,
            TargetAmount = ObfuscationHelper.Obfuscate(goal.TargetAmount),
            EarmarkedAmount = ObfuscationHelper.Obfuscate(goal.EarmarkedAmount),
            TargetDate = goal.TargetDate,
            Priority = goal.Priority,
            Status = goal.Status,
            IsRecurring = goal.IsRecurring,
            RecurrenceMonths = goal.RecurrenceMonths,
            LastFundedCycleKey = goal.LastFundedCycleKey,
            CreatedAt = goal.CreatedAt,
            CompletedAt = goal.CompletedAt
        };
    }

    internal static SavingsGoalPoolDto MapToDto(SavingsGoalPoolSummary summary)
    {
        return new SavingsGoalPoolDto
        {
            RewardsBalance = ObfuscationHelper.Obfuscate(summary.RewardsBalance),
            TotalEarmarked = ObfuscationHelper.Obfuscate(summary.TotalEarmarked),
            Unassigned = ObfuscationHelper.Obfuscate(summary.Unassigned),
            RequiredPerCycleTotal = ObfuscationHelper.Obfuscate(summary.RequiredPerCycleTotal),
            CurrentCycleKey = summary.CurrentCycleKey
        };
    }

    private static decimal ReadWireAmount(JsonElement amount)
    {
        return amount.ValueKind switch
        {
            JsonValueKind.String => ObfuscationHelper.Deobfuscate(amount.GetString() ?? string.Empty),
            JsonValueKind.Number when amount.TryGetDecimal(out var value) => value,
            _ => 0m
        };
    }
}

public class SavingsGoalMutationDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public JsonElement TargetAmount { get; set; }
    public JsonElement EarmarkedAmount { get; set; }
    public string? TargetDate { get; set; }
    public string Priority { get; set; } = "Medium";
    public bool IsRecurring { get; set; }
    public int RecurrenceMonths { get; set; } = 12;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? ClientKey { get; set; }
}

public class SavingsGoalContributionDto
{
    public JsonElement Amount { get; set; }
}

public class SavingsGoalDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TargetAmount { get; set; } = string.Empty;
    public string EarmarkedAmount { get; set; } = string.Empty;
    public DateTime TargetDate { get; set; }
    public string Priority { get; set; } = "Medium";
    public string Status { get; set; } = SavingsGoalStatus.Active;
    public bool IsRecurring { get; set; }
    public int RecurrenceMonths { get; set; }
    public string? LastFundedCycleKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class SavingsGoalPoolDto
{
    public string RewardsBalance { get; set; } = string.Empty;
    public string TotalEarmarked { get; set; } = string.Empty;
    public string Unassigned { get; set; } = string.Empty;
    public string RequiredPerCycleTotal { get; set; } = string.Empty;
    public string CurrentCycleKey { get; set; } = string.Empty;
}
