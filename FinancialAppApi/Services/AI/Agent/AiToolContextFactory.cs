using FinancialAppApi.Database;
using FinancialAppApi.Services.AI.Tools;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.AI.Agent;

// Builds the per-turn tool context from the user's settings. Sensitive mode is on when the saved
// setting says so or the client asks for it (a stale client can hide data but never reveal it),
// and it defaults to on when no settings row exists yet.
public sealed class AiToolContextFactory
{
    private readonly AppDbContext _context;
    private readonly FinancialClock _clock;

    public AiToolContextFactory(AppDbContext context, FinancialClock clock)
    {
        _context = context;
        _clock = clock;
    }

    public async Task<AiToolContext> CreateAsync(bool forceSensitiveMode, CancellationToken cancellationToken)
    {
        var setting = await _context.FinancialSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return new AiToolContext(
            forceSensitiveMode || (setting?.HideSensitive ?? true),
            setting?.CycleDay ?? FinancialConstants.DefaultCycleDay,
            setting?.Currency ?? "USD",
            _clock.Today,
            ReadSensitiveSettingAsync);
    }

    private async Task<bool> ReadSensitiveSettingAsync(CancellationToken cancellationToken) =>
        await _context.FinancialSettings.AsNoTracking().Select(setting => (bool?)setting.HideSensitive).FirstOrDefaultAsync(cancellationToken) ?? true;
}
