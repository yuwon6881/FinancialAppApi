using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FinancialAppApi.Database;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Push;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using FinancialAppApi.Diagnostics;

namespace FinancialAppApi.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApiInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddControllers().AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        services.AddMemoryCache();

        // Swagger UI is only mapped in Development (Program.cs); skip the generator's
        // assembly load + ApiExplorer model build on production cold starts.
        if (environment.IsDevelopment())
        {
            services.AddEndpointsApiExplorer();
            services.AddSwaggerGen();
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(Telemetry.ServiceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(Telemetry.ServiceName)
                    .AddAspNetCoreInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation(options =>
                    {
                        options.SetDbStatementForText = true;
                    });

                // The console exporter dumps every span to stdout; it's a local-dev aid.
                // In production (Cloud Run) it floods Cloud Logging and inflates ingestion cost.
                if (environment.IsDevelopment())
                {
                    tracing.AddConsoleExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(Telemetry.ServiceName)
                    .AddAspNetCoreInstrumentation();

                if (environment.IsDevelopment())
                {
                    metrics.AddConsoleExporter();
                }
            });

        services.AddHealthChecks()
            .AddNpgSql(configuration.GetConnectionString("DefaultConnection") ?? "", tags: ["ready"]);

        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
        });
        services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);

        var corsOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? configuration.GetSection("WebAuthn:AllowedOrigins").Get<string[]>()
            ?? [];
        services.AddCors(options => options.AddPolicy("AllowFrontend", policy =>
        {
            if (corsOrigins.Length > 0)
            {
                policy.WithOrigins(corsOrigins)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .WithExposedHeaders(AuthCookieService.CsrfHeaderName)
                      .AllowCredentials();
            }
            else if (environment.IsDevelopment())
            {
                // Local dev convenience only: reflect any origin with credentials so the Vite dev
                // server (whatever port/host) can exercise cookie auth.
                policy.SetIsOriginAllowed(origin => true)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .WithExposedHeaders(AuthCookieService.CsrfHeaderName)
                      .AllowCredentials();
            }
            else
            {
                // Outside Development an empty origin list is a misconfiguration. Never combine a
                // reflected origin with AllowCredentials in production — that would defeat CSRF/CORS
                // for cookie-authenticated requests. Allow anonymous cross-origin reads only.
                policy.SetIsOriginAllowed(origin => true)
                      .AllowAnyHeader()
                      .AllowAnyMethod();
            }
        }));

        return services;
    }

    public static IServiceCollection AddAiServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var requestsPerMinute = configuration.GetValue("Ai:RequestsPerMinute", 20);
        // OCR receipt scans each trigger a Gemini vision call (pricier than a chat turn) and write
        // ~13 MB of base64 to Postgres, so cap them harder than chat by default.
        var ocrRequestsPerMinute = configuration.GetValue("Ocr:RequestsPerMinute", 10);
        var passwordVerificationRequestsPerMinute =
            configuration.GetValue("Auth:PasswordVerificationRequestsPerMinute", 30);
        services.AddRateLimiter(options =>
        {
            static string PartitionKeyFor(HttpContext httpContext)
            {
                var authorization = httpContext.Request.Headers.Authorization.ToString();
                return string.IsNullOrWhiteSpace(authorization)
                    ? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous"
                    : authorization;
            }

            options.AddPolicy("ai", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(PartitionKeyFor(httpContext), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, requestsPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));

            // Partition on "ocr:"+key so an OCR burst doesn't consume the same window as chat.
            options.AddPolicy("ocr", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter("ocr:" + PartitionKeyFor(httpContext), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, ocrRequestsPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));

            options.AddPolicy("password-verification", httpContext =>
            {
                var partitionKey = httpContext.Request.TryGetBearerToken(out var token) == BearerTokenResult.Ok
                    ? token
                    : httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter(
                    "password-verification:" + partitionKey,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, passwordVerificationRequestsPerMinute),
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                // The AI chat client expects a {reply, actions} shape even on rejection; other
                // endpoints (OCR) get a plain {message}.
                if (context.HttpContext.Request.Path.StartsWithSegments("/api/ai"))
                {
                    await context.HttpContext.Response.WriteAsJsonAsync(new
                    {
                        reply = "Ask AI is receiving too many requests. Please wait a moment and try again.",
                        actions = Array.Empty<object>()
                    }, cancellationToken);
                }
                else
                {
                    await context.HttpContext.Response.WriteAsJsonAsync(new
                    {
                        message = "Too many requests. Please wait a moment and try again."
                    }, cancellationToken);
                }
            };
        });

        services.AddHttpClient<AiClient>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<ReceiptScanTaskDispatcher>(client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient(SupabaseReceiptImageStore.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FinancialAppApi/1.0");
        });
        services.AddSingleton<IReceiptImageStore, SupabaseReceiptImageStore>();
        services.AddSingleton<ReceiptScanRetentionPolicy>();
        services.AddScoped<ReceiptScanProcessor>();
        services.AddScoped<ReceiptScanJobCleanupService>();
        services.AddScoped<CategoryCleanupApplier>();
        services.AddScoped<CategorySuggestionService>();
        services.AddScoped<AiAssistantService>();
        services.AddSingleton<ReceiptScanQueue>();
        services.AddHostedService<ReceiptScanBackgroundService>();
        services.AddHostedService<ReceiptScanCleanupBackgroundService>();
        services.AddScoped<OcrScanJobService>();
        return services;
    }

    public static IServiceCollection AddAuthServices(this IServiceCollection services)
    {
        services.AddScoped<AuthSessionService>();
        services.AddScoped<AuthAccountService>();
        services.AddScoped<WebAuthnService>();
        services.AddSingleton<AuthCookieService>();
        services.AddSingleton<TotpService>();
        services.AddSingleton<SecretProtector>();
        services.AddScoped<RecoveryCodeService>();
        return services;
    }

    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        bool migrateOnly)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection must be configured.");
        }

        if (migrateOnly && connectionString.Contains("Port=6543", StringComparison.OrdinalIgnoreCase))
        {
            connectionString = connectionString.Replace("Port=6543", "Port=5432", StringComparison.OrdinalIgnoreCase);
        }

        var npgsqlConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = configuration.GetValue("Database:MaxPoolSize", 8),
            ConnectionIdleLifetime = 30,
            ConnectionPruningInterval = 5,
            MaxAutoPrepare = 0,
            NoResetOnClose = true,
            Timeout = 5,
            CommandTimeout = 15,
        }.ConnectionString;

        services.AddDbContext<AppDbContext>(options =>
        {
            options.UseNpgsql(npgsqlConnectionString, npgsql => npgsql.EnableRetryOnFailure());
        });
        var dataProtection = services.AddDataProtection()
            .SetApplicationName("FinancialAppApi")
            .PersistKeysToDbContext<AppDbContext>();

        var certificateBase64 = configuration["DataProtection:CertificateBase64"];
        var certificatePath = configuration["DataProtection:CertificatePath"];
        var certificatePassword = configuration["DataProtection:CertificatePassword"];
        X509Certificate2? keyEncryptionCertificate = null;
        if (!string.IsNullOrWhiteSpace(certificateBase64))
        {
            keyEncryptionCertificate = X509CertificateLoader.LoadPkcs12(
                Convert.FromBase64String(certificateBase64),
                certificatePassword,
                X509KeyStorageFlags.EphemeralKeySet);
        }
        else if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            keyEncryptionCertificate = X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath,
                certificatePassword,
                X509KeyStorageFlags.EphemeralKeySet);
        }

        if (keyEncryptionCertificate != null)
        {
            dataProtection.ProtectKeysWithCertificate(keyEncryptionCertificate);
        }

        services.AddSingleton<FinancialClock>();
        services.AddScoped<CycleBalanceService>();
        services.AddScoped<RecurringOccurrenceService>();
        services.AddScoped<RecurringPaymentAlertService>();
        services.AddScoped<RecurringPaymentService>();
        services.AddScoped<RecurringPaymentPayEarlyService>();
        services.AddScoped<TransactionCategoryService>();
        services.AddScoped<WishlistService>();
        services.AddScoped<TransactionPersistenceService>();
        services.AddScoped<TransactionQueryService>();
        services.AddScoped<FinancialService>();
        services.AddScoped<PushSubscriptionService>();
        return services;
    }

    public static IServiceCollection AddPushServices(this IServiceCollection services)
    {
        services.AddSingleton<IGoogleIdTokenVerifier, GoogleIdTokenVerifier>();
        services.AddSingleton<IGoogleOidcTokenValidator, GoogleOidcTokenValidator>();
        services.AddHttpClient<IFcmPushSender, FcmHttpV1PushSender>(client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<PushDispatchService>();
        return services;
    }
}
