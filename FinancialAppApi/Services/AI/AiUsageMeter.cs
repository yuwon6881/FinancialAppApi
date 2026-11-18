using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.AI;

// Per-user daily token accounting for Ask AI. Days are UTC so usage aggregates at
// one fixed instant regardless of the user's time zone.
public sealed class AiUsageMeter
{
    private readonly AppDbContext _context;
    private readonly TimeProvider _time;

    public AiUsageMeter(AppDbContext context, TimeProvider? time = null)
    {
        _context = context;
        _time = time ?? TimeProvider.System;
    }

    public async Task RecordAsync(AiTokenUsage usage, int calls, CancellationToken cancellationToken)
    {
        if (calls <= 0) return;
        var now = _time.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var todayRows = _context.AiUsageDays.Where(day => day.Date == today);
        var relational = _context.Database.IsRelational();
        // Two turns finishing together can both try to create today's row; the loser retries as
        // an atomic increment against the row the winner created.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (relational && await todayRows.ExecuteUpdateAsync(
                    update => update
                        .SetProperty(day => day.InputTokens, day => day.InputTokens + usage.InputTokens)
                        .SetProperty(day => day.CachedTokens, day => day.CachedTokens + usage.CachedTokens)
                        .SetProperty(day => day.OutputTokens, day => day.OutputTokens + usage.OutputTokens)
                        .SetProperty(day => day.ReasoningTokens, day => day.ReasoningTokens + usage.ReasoningTokens)
                        .SetProperty(day => day.Calls, day => day.Calls + calls)
                        .SetProperty(day => day.UpdatedAt, now),
                    cancellationToken) > 0) return;

            var row = relational ? null : await todayRows.FirstOrDefaultAsync(cancellationToken);
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
            row.UpdatedAt = now;
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
