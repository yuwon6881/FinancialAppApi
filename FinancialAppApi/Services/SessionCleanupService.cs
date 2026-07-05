using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;

namespace FinancialAppApi.Services;

// Expired UserSession/WebAuthnChallenge rows were previously only pruned as
// a side effect of someone happening to log in (see AuthController.Login /
// WebAuthnController.LoginVerify) -- table hygiene had nothing to do with
// login frequency for no good reason. This sweeps both on a fixed schedule
// instead, independent of request activity.
public class SessionCleanupService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionCleanupService> _logger;

    public SessionCleanupService(IServiceScopeFactory scopeFactory, ILogger<SessionCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session cleanup sweep failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        var expiredSessions = await context.UserSessions.Where(s => s.ExpiresAt < now).ToListAsync(stoppingToken);
        if (expiredSessions.Count > 0)
        {
            context.UserSessions.RemoveRange(expiredSessions);
        }

        var expiredChallenges = await context.WebAuthnChallenges.Where(c => c.ExpiresAt < now).ToListAsync(stoppingToken);
        if (expiredChallenges.Count > 0)
        {
            context.WebAuthnChallenges.RemoveRange(expiredChallenges);
        }

        if (expiredSessions.Count > 0 || expiredChallenges.Count > 0)
        {
            await context.SaveChangesAsync(stoppingToken);
            _logger.LogInformation(
                "Session cleanup sweep removed {SessionCount} expired session(s) and {ChallengeCount} expired challenge(s).",
                expiredSessions.Count, expiredChallenges.Count);
        }
    }
}
