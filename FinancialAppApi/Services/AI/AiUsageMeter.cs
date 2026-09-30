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

    private DateOnly Today => DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

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
