using FinancialAppApi.Database;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

/// <summary>
/// Moves the once-per-container costs that would otherwise land inside the first request off the
/// request path: the EF Core model build, the first PostgreSQL connection, and the DataProtection
/// key-ring read. Cloud Run runs this service at scale-to-zero, so every cold start pays all three.
/// </summary>
public sealed class StartupWarmupService(
    IServiceScopeFactory scopeFactory,
    ILogger<StartupWarmupService> logger) : IHostedService
{
    // Distinct from every real protector purpose so warming can never produce a payload that
    // some other part of the app would accept as genuine.
    private const string WarmupPurpose = "FinancialAppApi.StartupWarmup";

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Deliberately not awaited. The point is to overlap this work with Kestrel binding and
        // with the request that triggered the cold start; awaiting would move the cost earlier
        // rather than hide it, and would delay the port opening that Cloud Run measures.
        _ = Task.Run(() => WarmAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WarmAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Per Services/README.md: a hosted service is a singleton, so scoped dependencies
            // must come from a scope created per work item rather than the constructor.
            using var scope = scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // EF builds the model lazily on first access and caches it for the process. With 35
            // entity types and a global query filter on each user-owned one, this is the largest
            // single item here. Compiled models would remove it outright, but EF still refuses to
            // generate one for a context that uses query filters -- and the filters are what make
            // tenancy fail closed, so this stays a warm-up rather than a build-time fix.
            _ = context.Model;

            // Pays the TLS handshake against the pooler and Npgsql's first-use type-handler setup.
            // Closing returns the connection to the pool physically open (NoResetOnClose, and a
            // 30s ConnectionIdleLifetime), so the first real request reuses it.
            await context.Database.OpenConnectionAsync(cancellationToken);
            await context.Database.CloseConnectionAsync();

            // PersistKeysToDbContext reads the key ring lazily on the first Protect/Unprotect, an
            // extra round trip serialized inside the first request touching a session cookie or an
            // encrypted secret. Unlike the two above, this one cannot race the request for the
            // same work, so it is a guaranteed saving.
            _ = scope.ServiceProvider
                .GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(WarmupPurpose)
                .Protect("warm");
        }
        catch (Exception exception)
        {
            // A warm-up is an optimisation, never a dependency. A database that is briefly
            // unreachable must degrade to a slower first request, not a failed container start.
            logger.LogWarning(exception, "Startup warm-up did not complete; the first request will pay these costs instead.");
        }
    }
}
