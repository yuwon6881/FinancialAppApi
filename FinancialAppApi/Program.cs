using FinancialAppApi.Database;
using FinancialAppApi.Extensions;
using FinancialAppApi.Middleware;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;

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
    .AddPersistence(builder.Configuration, migrateOnly);

builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();

app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString();
    context.Response.Headers["X-Correlation-ID"] = correlationId;
    using var scope = app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
    await next();
});

app.UseResponseCompression();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowFrontend");
app.UseMiddleware<CsrfProtectionMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();
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

app.Run();

// Exposed so integration tests can boot the real app via WebApplicationFactory<Program>.
// Program is otherwise an implicitly-internal top-level-statements class.
public partial class Program;
