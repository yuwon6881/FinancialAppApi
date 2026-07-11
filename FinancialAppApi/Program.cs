using FinancialAppApi.Database;
using FinancialAppApi.Extensions;
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
    .AddApiInfrastructure(builder.Configuration)
    .AddAiServices(builder.Configuration)
    .AddAuthServices()
    .AddPersistence(builder.Configuration, migrateOnly);

var app = builder.Build();

app.UseResponseCompression();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowFrontend");
app.UseRateLimiter();
app.UseAuthorization();
app.MapGet("/api/ping", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));
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
            DbSeeder.Seed(context);
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
