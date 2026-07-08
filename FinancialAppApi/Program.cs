using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.ResponseCompression;
using System.IO.Compression;
using System.Text.Json.Serialization;
using Npgsql;
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
//
// DefaultIgnoreCondition drops null-valued properties from every response body
// (e.g. an unset RecurringPaymentId), which trims bytes off every transaction row
// sent over the wire -- meaningful for Vercel's free bandwidth budget.
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

// In-process cache for reference/aggregate data that changes rarely relative to
// how often it is read (transaction categories, cycle balance snapshots). Avoids
// a distributed cache we can't afford on the free tier; safe because it only ever
// holds data that is also authoritatively persisted in Postgres.
builder.Services.AddMemoryCache();

// Response compression (Brotli + gzip). Cloud Run does not compress responses for
// you, so without this every JSON payload goes out uncompressed. Enabled for HTTPS
// since our API is only served over TLS.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/json" });
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

builder.Services.AddScoped<ReceiptScanProcessor>();
builder.Services.AddSingleton<ReceiptScanTaskDispatcher>();
// Bounded in-process work queue + single consumer for the OCR fallback path, so a
// burst of receipt uploads can't spawn unbounded concurrent Gemini calls on a
// 1-vCPU free-tier container. Cloud Tasks remains the preferred path when configured.
builder.Services.AddSingleton<ReceiptScanQueue>();
builder.Services.AddHostedService<ReceiptScanBackgroundService>();
builder.Services.AddScoped<CycleBalanceService>();
builder.Services.AddSingleton<TotpService>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddScoped<RecoveryCodeService>();

// Configure PostgreSQL database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:DefaultConnection must be configured.");
}

// Free-tier Postgres (Supabase) has a very low concurrent-connection ceiling, and
// Cloud Run can run several container instances at once -- so each instance must
// keep its physical connection count tiny. The runtime connection string points at
// Supabase's Supavisor transaction-mode pooler (port 6543), which shares a handful of
// real Postgres connections across many clients; migrations use session mode (5432),
// wired in cloudbuild.yaml.
//
// Transaction-mode pooling constraints (see Supabase/Npgsql docs):
//   * MaxAutoPrepare = 0 and NoResetOnClose = true -- server-side prepared statements
//     and DISCARD ALL don't survive the pooler handing each transaction a different
//     backend connection, so they must be disabled.
//   * NO client-side Multiplexing -- combining Npgsql multiplexing with Supavisor's
//     transaction-mode multiplexing is precisely what breaks prepared statements. The
//     pooler already does the connection-sharing job.
// MinPoolSize = 0 lets an idle, scaled-to-zero instance hold no connections at all.
var npgsqlConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
{
    Pooling = true,
    MinPoolSize = 0,
    MaxPoolSize = builder.Configuration.GetValue("Database:MaxPoolSize", 8),
    ConnectionIdleLifetime = 30,
    ConnectionPruningInterval = 5,
    MaxAutoPrepare = 0,
    NoResetOnClose = true,
    Timeout = 5,
    CommandTimeout = 15,
}.ConnectionString;

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(npgsqlConnectionString, npgsqlOptions => npgsqlOptions.EnableRetryOnFailure());
});

// Cloud Run instances are ephemeral and can run multiple replicas at once, so the
// default Data Protection key ring (in-memory/local-disk) is per-instance and gets
// lost on restart, redeploy, or when a request lands on a different replica than the
// one that issued it -- this is what causes "key {guid} was not found in the key
// ring" when unprotecting TOTP secrets. Persisting keys to Postgres makes the ring
// durable and shared across every instance. SetApplicationName pins the key isolation
// namespace so it survives redeploys (it would otherwise default to the content root
// path, which can change between revisions).
builder.Services.AddDataProtection()
    .SetApplicationName("FinancialAppApi")
    .PersistKeysToDbContext<AppDbContext>();

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

// Compress responses before anything else writes to the body.
app.UseResponseCompression();

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
