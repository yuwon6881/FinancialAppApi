using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Services;

var builder = WebApplication.CreateBuilder(args);
var seedDatabase = args.Contains("--seed-database", StringComparer.OrdinalIgnoreCase);

// Cloud Run (and most container PaaS) tell the app which port to listen on
// via the PORT env var. Bind to it when present; otherwise fall back to the
// ASPNETCORE_URLS default used for local/Docker runs.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddScoped<ReceiptScanProcessor>();
builder.Services.AddSingleton<ReceiptScanTaskDispatcher>();
builder.Services.AddScoped<CycleBalanceService>();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<TotpService>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddScoped<RecoveryCodeService>();
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();

// Configure PostgreSQL database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:DefaultConnection must be configured.");
}

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.EnableRetryOnFailure());
});

// Expired sessions/challenges are pruned lazily on auth activity (login,
// per-request token checks, and each WebAuthn options call), and live rows
// are bounded per-device -- so no always-on background sweeper is needed.
// This lets the service scale to zero on Cloud Run without leaking rows.

// Configure Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure CORS. Production is locked to the configured frontend origin(s)
// (Cors:AllowedOrigins, falling back to the WebAuthn origins so there's a
// single place to list the frontend). With none configured -- e.g. local dev
// -- it stays permissive so a localhost frontend still works.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? builder.Configuration.GetSection("WebAuthn:AllowedOrigins").Get<string[]>()
    ?? Array.Empty<string>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        if (corsOrigins.Length > 0)
        {
            policy.WithOrigins(corsOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
        else
        {
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowFrontend");

app.UseAuthorization();

app.MapGet("/api/ping", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.MapControllers();

// Database maintenance is normally handled during deployment. Keeping it out
// of Cloud Run startup avoids failed rollouts when the database connection is
// briefly slow during the revision health check.
var migrateOnStartup = app.Configuration.GetValue("Database:MigrateOnStartup", false);
var seedOnStartup = app.Configuration.GetValue("Database:SeedOnStartup", false);
if (migrateOnStartup || seedOnStartup || seedDatabase)
{
    using (var scope = app.Services.CreateScope())
    {
        var services = scope.ServiceProvider;
        try
        {
            var context = services.GetRequiredService<AppDbContext>();
            if (migrateOnStartup)
            {
                context.Database.Migrate();
            }

            if (seedOnStartup || seedDatabase)
            {
                DbSeeder.Seed(context);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("CRITICAL DATABASE MIGRATION/SEEDING ERROR:");
            Console.WriteLine(ex.ToString());
            var logger = services.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "An error occurred while migrating or seeding the database.");
            throw;
        }
    }

    if (seedDatabase)
    {
        return;
    }
}

app.Run();
