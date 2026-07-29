using FinancialAppApi.Database;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using FinancialAppApi.Services.Documents;

namespace FinancialAppApi.Tests.Integration;

/// <summary>
/// Boots the real API (Program.cs, full middleware pipeline, routing, JSON serialization,
/// custom bearer-token auth) but swaps PostgreSQL for an isolated EF Core InMemory store and
/// removes the OCR background worker so no test ever reaches Google Gemini.
///
/// Each factory instance gets its own InMemory database name, and each xUnit test gets its own
/// factory (see <see cref="IntegrationTestBase"/>), so tests are fully isolated from one another.
/// </summary>
public sealed class FinancialApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = "it-" + Guid.NewGuid().ToString("N");
    private string? _lastAiRequestBody;

    /// <summary>The most recent prompt sent to the AI provider by this test host.</summary>
    public string? LastAiRequestBody => Volatile.Read(ref _lastAiRequestBody);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // The default Windows Event Log provider is enabled by the Release host and requires
        // machine-level write access. Integration tests must remain runnable by unprivileged
        // developers and CI workers, so keep the test host silent and provider-independent.
        builder.ConfigureLogging(logging => logging.ClearProviders());

        // AddPersistence() throws if no connection string is present, and it runs before our
        // ConfigureTestServices override, so supply a dummy one to get past the guard.
        builder.UseSetting("ConnectionStrings:DefaultConnection",
            "Host=localhost;Database=unused;Username=unused;Password=unused");
        builder.UseSetting("AiApiKey", "test-key");

        // Pin the registration cap so HTTP registration-gating tests stay deterministic
        // regardless of the shipped appsettings default (which product config may change).
        builder.UseSetting("Auth:MaxUsers", "1");

        builder.ConfigureTestServices(services =>
        {
            // Drop the Npgsql AppDbContext registration (options + context + pooling internals).
            var toRemove = services
                .Where(d =>
                    d.ServiceType == typeof(DbContextOptions<AppDbContext>) ||
                    d.ServiceType == typeof(DbContextOptions) ||
                    d.ServiceType == typeof(AppDbContext) ||
                    (d.ServiceType.FullName?.Contains("DbContextOptions") ?? false))
                .ToList();
            foreach (var descriptor in toRemove)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseInMemoryDatabase(_databaseName)
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
            });

            // Keep AI endpoint tests hermetic while allowing them to inspect the exact provider
            // payload generated through the real HTTP, auth, and application-service pipeline.
            services.AddHttpClient<AiClient>()
                .ConfigurePrimaryHttpMessageHandler(() => new CapturingAiHandler(this));

            var imageStoreRegistrations = services
                .Where(descriptor => descriptor.ServiceType == typeof(IReceiptImageStore))
                .ToList();
            foreach (var descriptor in imageStoreRegistrations)
            {
                services.Remove(descriptor);
            }
            services.AddSingleton<IReceiptImageStore, FakeReceiptImageStore>();

            var vaultStoreRegistrations = services
                .Where(descriptor => descriptor.ServiceType == typeof(IDocumentVaultStore))
                .ToList();
            foreach (var descriptor in vaultStoreRegistrations)
            {
                services.Remove(descriptor);
            }
            services.AddSingleton<IDocumentVaultStore, FakeDocumentVaultStore>();

            // The receipt-scan background worker would drain the queue and call Gemini for real.
            // Remove it so OCR tests stay hermetic; jobs simply remain "queued".
            var hostedService = services.FirstOrDefault(d =>
                d.ServiceType == typeof(IHostedService) &&
                d.ImplementationType == typeof(ReceiptScanBackgroundService));
            if (hostedService is not null)
            {
                services.Remove(hostedService);
            }
        });
    }

    /// <summary>Runs an action against a fresh DB scope (for seeding / assertions).</summary>
    public async Task WithDbContextAsync(Func<AppDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userIds = await db.AppUsers.AsNoTracking().Select(user => user.Id).Take(2).ToListAsync();
        if (userIds.Count == 1)
        {
            db.SetCurrentUser(userIds[0]);
        }
        await action(db);
    }

    private sealed class CapturingAiHandler(FinancialApiFactory factory) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            Volatile.Write(ref factory._lastAiRequestBody, requestBody);

            const string responseBody = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{\\\"reply\\\":\\\"Context received.\\\",\\\"closeChat\\\":false,\\\"actions\\\":[]}\"}]},\"finishReason\":\"STOP\"}]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
