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
using FinancialAppApi.Services.Documents;
using FinancialAppApi.Services.Investments;

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
                        // SQL text can reveal schema and query intent. Aggregate durations and
                        // counts are recorded by PerformanceDbCommandInterceptor instead.
                        options.SetDbStatementForText = false;
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

        services.AddHealthChecks();

        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
        });
        // Fastest, deliberately and measured. Raising Brotli to Optimal was evaluated on a
        // representative ledger payload (85 kB of JSON): Fastest produced 3.85 kB and Optimal
        // 3.84 kB — no meaningful size reduction — for roughly double the compression CPU.
        // This API's JSON is repetitive enough that the cheapest quality already gets ~95% of
        // the way, and CPU is the constrained resource on Cloud Run. SmallestSize is far worse
        // still (~120 ms for the same payload). Re-measure before changing this.
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
        // OCR receipt scans each trigger a vision call (pricier than a chat turn) and write
        // ~13 MB of base64 to Postgres, so cap them harder than chat by default.
        var ocrRequestsPerMinute = configuration.GetValue("Ocr:RequestsPerMinute", 10);
        var passwordVerificationRequestsPerMinute =
            configuration.GetValue("Auth:PasswordVerificationRequestsPerMinute", 30);
        var documentRequestsPerMinute =
            configuration.GetValue("Documents:RequestsPerMinute", 60);
        services.AddRateLimiter(options =>
        {
            static string PartitionKeyFor(HttpContext httpContext)
            {
                var authorization = httpContext.Request.Headers.Authorization.ToString();
                if (!string.IsNullOrWhiteSpace(authorization)) return authorization;
                // Web PWA clients authenticate with a cookie and send no Authorization
                // header. Falling through to RemoteIpAddress would put every one of them
                // behind the Vercel proxy -> Cloud Run into a single shared bucket, so one
                // user's chat session would rate-limit everybody else.
                var sessionCookie = httpContext.Request.Cookies[AuthCookieService.AuthCookieName];
                if (!string.IsNullOrWhiteSpace(sessionCookie)) return "cookie:" + sessionCookie;
                return httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
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

            options.AddPolicy("documents", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter("documents:" + PartitionKeyFor(httpContext), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, documentRequestsPerMinute),
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
        services.AddScoped<AiConversationMemoryService>();
        services.AddScoped<AiAssistantService>();
        services.AddSingleton<ReceiptScanQueue>();
        services.AddHostedService<ReceiptScanBackgroundService>();
        services.AddHostedService<ReceiptScanCleanupBackgroundService>();
        services.AddScoped<OcrScanJobService>();
        
        services.AddHttpClient(GcsDocumentVaultStore.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FinancialAppApi/1.0");
        });
        services.AddSingleton<IDocumentVaultStore, GcsDocumentVaultStore>();
        services.AddScoped<DocumentVaultService>();
        services.AddScoped<VaultAmountExtractor>();
        services.Configure<DocumentVaultOptions>(configuration.GetSection("DocumentVault"));

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

    private static string NormalizeConnectionString(string connectionString, bool migrateOnly)
    {
        if (connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
            connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(connectionString);
            var userInfoParts = uri.UserInfo.Split(':', 2);
            var username = Uri.UnescapeDataString(userInfoParts[0]);
            var password = userInfoParts.Length > 1 ? Uri.UnescapeDataString(userInfoParts[1]) : "";
            var database = uri.AbsolutePath.TrimStart('/');
            var port = uri.Port > 0 ? uri.Port : 5432;

            if (migrateOnly && port == 6543)
            {
                port = 5432;
            }

            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = port,
                Database = database,
                Username = username,
                Password = password,
                SslMode = SslMode.Require
            };

            if (!string.IsNullOrEmpty(uri.Query))
            {
                var query = uri.Query.TrimStart('?');
                foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split('=', 2);
                    if (kv.Length == 2)
                    {
                        builder[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
                    }
                }
            }

            return builder.ConnectionString;
        }

        if (migrateOnly)
        {
            if (connectionString.Contains("Port=6543", StringComparison.OrdinalIgnoreCase))
            {
                connectionString = connectionString.Replace("Port=6543", "Port=5432", StringComparison.OrdinalIgnoreCase);
            }
            else if (connectionString.Contains(":6543", StringComparison.OrdinalIgnoreCase))
            {
                connectionString = connectionString.Replace(":6543", ":5432", StringComparison.OrdinalIgnoreCase);
            }
        }

        return connectionString;
    }

    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        bool migrateOnly)
    {
        var rawConnectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(rawConnectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection must be configured.");
        }

        var connectionString = NormalizeConnectionString(rawConnectionString, migrateOnly);

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
            SslMode = SslMode.Require,
        }.ConnectionString;

        services.AddHealthChecks()
            .AddNpgSql(npgsqlConnectionString, tags: ["ready"]);

        services.AddScoped<RequestPerformanceContext>();
        services.AddScoped<PerformanceDbCommandInterceptor>();
        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            options.UseNpgsql(npgsqlConnectionString, npgsql => npgsql.EnableRetryOnFailure());
            options.AddInterceptors(serviceProvider.GetRequiredService<PerformanceDbCommandInterceptor>());
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
        services.AddScoped<Services.Stability.StabilityRecoveryService>();
        services.AddScoped<RecurringOccurrenceService>();
        services.AddScoped<RecurringPaymentAlertService>();
        services.AddScoped<RecurringPaymentService>();
        services.AddScoped<RecurringPaymentPayEarlyService>();
        services.AddScoped<TransactionCategoryService>();
        services.AddScoped<WishlistService>();
        services.AddScoped<Services.SavingsGoals.SavingsGoalService>();
        services.AddScoped<TransactionPersistenceService>();
        services.AddScoped<TransactionQueryService>();
        services.AddScoped<FinancialService>();
        services.AddScoped<PushSubscriptionService>();
        services.Configure<MarketDataOptions>(configuration.GetSection("MarketData"));
        services.AddOptions<TwelveDataMarketDataOptions>()
            .Bind(configuration.GetSection("MarketData:Providers:TwelveData"))
            .PostConfigure(options =>
            {
                // One-release compatibility for existing Cloud Run settings. New deployments use
                // MarketData__Providers__TwelveData__* exclusively.
                if (configuration["MarketData:Providers:TwelveData:Enabled"] is null)
                {
                    options.Enabled = configuration.GetValue("MarketData:Enabled", options.Enabled);
                }

                options.ApiKey ??= configuration["MarketData:TwelveDataApiKey"];
                options.BaseUrl = configuration["MarketData:Providers:TwelveData:BaseUrl"]
                    ?? configuration["MarketData:BaseUrl"]
                    ?? options.BaseUrl;
                options.RefreshCallsPerMinute = configuration.GetValue(
                    "MarketData:Providers:TwelveData:RefreshCallsPerMinute",
                    configuration.GetValue("MarketData:RefreshCallsPerMinute", options.RefreshCallsPerMinute));
                options.DailyCallCeiling = configuration.GetValue(
                    "MarketData:Providers:TwelveData:DailyCallCeiling",
                    configuration.GetValue("MarketData:DailyCallCeiling", options.DailyCallCeiling));
                options.PerUserDailyCallCeiling = configuration.GetValue(
                    "MarketData:Providers:TwelveData:PerUserDailyCallCeiling",
                    configuration.GetValue("MarketData:PerUserDailyCallCeiling", options.PerUserDailyCallCeiling));
            });
        services.AddHttpClient<TwelveDataMarketDataProvider>((serviceProvider, client) =>
        {
            var marketData = serviceProvider.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<TwelveDataMarketDataOptions>>().Value;
            client.BaseAddress = new Uri(marketData.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FinancialAppApi/1.0");
        });
        services.AddScoped(provider => new MarketDataProviderRegistration(
            provider.GetRequiredService<TwelveDataMarketDataProvider>()));
        services.AddScoped<MarketDataProviderRegistry>();
        services.AddScoped<IMarketDataProvider>(provider =>
            provider.GetRequiredService<MarketDataProviderRegistry>().ActiveProvider);
        services.AddScoped<InvestmentAccountingService>();
        services.AddScoped<InvestmentHistoryValidationService>();
        services.AddScoped<InvestmentPortfolioService>();
        services.AddScoped<InstrumentHistoryService>();
        services.AddScoped<InvestmentMarketDataService>();
        services.AddScoped<InvestmentMarketSearchService>();
        services.AddScoped<MarketDataQuotaService>();
        services.AddScoped<MarketDataCutoverService>();
        services.AddScoped<InvestmentAllocationService>();
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
