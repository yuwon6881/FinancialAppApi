using FinancialAppApi.Database;
using FinancialAppApi.Extensions;
using FinancialAppApi.Middleware;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Investments;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var seedDatabase = args.Contains("--seed-database", StringComparer.OrdinalIgnoreCase);
var migrateOnly = args.Contains("--migrate", StringComparer.OrdinalIgnoreCase);

var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

builder.Services
    .AddApiInfrastructure(builder.Configuration, builder.Environment)
    .AddAiServices(builder.Configuration)
    .AddAuthServices()
    .AddPersistence(builder.Configuration, migrateOnly)
    .AddPushServices();

builder.Services.AddProblemDetails();

var app = builder.Build();

// Resolve once at startup so an unknown or incapable active provider fails deployment before
// the first investment request. Missing credentials remain a supported manual-data mode.
using (var providerValidationScope = app.Services.CreateScope())
{
    _ = providerValidationScope.ServiceProvider.GetRequiredService<MarketDataProviderRegistry>().ActiveProvider;
}

app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();

app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString();
    context.Response.Headers["X-Correlation-ID"] = correlationId;
    using var scope = app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
    await next();
});

app.UseMiddleware<RequestPerformanceMiddleware>();
app.UseResponseCompression();

// Inside compression on purpose: the ETag is computed over the uncompressed body, so the same
// payload keeps one identity regardless of which encoding a client negotiated.
app.UseMiddleware<ConditionalGetMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowFrontend");
app.UseMiddleware<CsrfProtectionMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();
app.UseMiddleware<CategoryLimitAlertMiddleware>();
app.MapGet("/api/ping", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
app.MapControllers();

var migrateOnStartup = app.Configuration.GetValue("Database:MigrateOnStartup", false);
var seedOnStartup = app.Configuration.GetValue("Database:SeedOnStartup", false);
if (migrateOnStartup || seedOnStartup || seedDatabase || migrateOnly)
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<AppDbContext>();
        context.UseSchemaMaintenanceCommandTimeout(app.Configuration);
        if (migrateOnStartup || migrateOnly)
        {
            context.Database.Migrate();
        }
        if (seedOnStartup || seedDatabase)
        {
            DbSeeder.Seed(context, services.GetRequiredService<FinancialClock>());
        }
    }
    catch (Exception exception)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogCritical(exception, "An error occurred while migrating or seeding the database.");
        throw;
    }

    if (seedDatabase || migrateOnly)
    {
        return;
    }
}

var backfillProvider = args.FirstOrDefault(value =>
    value.StartsWith("--backfill-market-provider=", StringComparison.OrdinalIgnoreCase))?
    .Split('=', 2)[1];
var validateProvider = args.FirstOrDefault(value =>
    value.StartsWith("--validate-market-provider=", StringComparison.OrdinalIgnoreCase))?
    .Split('=', 2)[1];
if (backfillProvider is not null && validateProvider is not null)
    throw new InvalidOperationException("Run market-data backfill and validation as separate commands.");
if (backfillProvider is not null || validateProvider is not null)
{
    using var scope = app.Services.CreateScope();
    var cutover = scope.ServiceProvider.GetRequiredService<MarketDataCutoverService>();
    object result = backfillProvider is not null
        ? await cutover.BackfillAsync(backfillProvider, CancellationToken.None)
        : await cutover.ValidateAsync(
            validateProvider!,
            args.Contains("--approve-market-data-differences", StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

app.Run();

// Exposed so integration tests can boot the real app via WebApplicationFactory<Program>.
// Program is otherwise an implicitly-internal top-level-statements class.
public partial class Program;
