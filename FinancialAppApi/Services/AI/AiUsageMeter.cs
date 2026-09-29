using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.AI;

// Per-user daily token accounting for Ask AI, and the budget that keeps one account (or one
// runaway client) from running up unbounded provider cost. Days are UTC so the budget resets at
// one fixed instant regardless of the user's time zone.
public sealed class AiUsageMeter
{
    public const long DefaultDailyTokenBudget = 1_500_000;

    private readonly AppDbContext _context;
    private readonly TimeProvider _time;
    private readonly long _dailyBudget;

    public AiUsageMeter(AppDbContext context, IConfiguration configuration, TimeProvider? time = null)
    {
        _context = context;
        _time = time ?? TimeProvider.System;
        _dailyBudget = Math.Max(0, configuration.GetValue("Ai:DailyTokenBudgetPerUser", DefaultDailyTokenBudget));
    }

    private DateOnly Today => DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

    // A budget of 0 disables the check. Counted as input plus output, which is what is billed.
    public async Task<bool> IsOverBudgetAsync(CancellationToken cancellationToken)
    {
        if (_dailyBudget == 0) return false;
        var today = Today;
        var used = await _context.AiUsageDays
            .AsNoTracking()
            .Where(day => day.Date == today)
            .Select(day => day.InputTokens + day.OutputTokens)
            .FirstOrDefaultAsync(cancellationToken);
        return used >= _dailyBudget;
    }

    public async Task RecordAsync(AiTokenUsage usage, int calls, CancellationToken cancellationToken)
    {
        if (calls <= 0) return;
        // Two turns finishing together can both try to create today's row; the loser retries as
        // an update against the row the winner created.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var today = Today;
            var row = await _context.AiUsageDays.FirstOrDefaultAsync(day => day.Date == today, cancellationToken);
            if (row == null)
            {
                row = new AiUsageDay { UserId = _context.RequireCurrentUserId(), Date = today };
                _context.AiUsageDays.Add(row);
            }
            row.InputTokens += usage.InputTokens;
            row.CachedTokens += usage.CachedTokens;
            row.OutputTokens += usage.OutputTokens;
            row.ReasoningTokens += usage.ReasoningTokens;
            row.Calls += calls;
            row.UpdatedAt = _time.GetUtcNow().UtcDateTime;
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                _context.Entry(row).State = EntityState.Detached;
            }
        }
    }
}
