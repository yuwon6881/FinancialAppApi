using System.Diagnostics;
using FinancialAppApi.Diagnostics;

namespace FinancialAppApi.Middleware;

/// <summary>
/// Emits one sanitized structured performance event for each API request.
/// </summary>
public sealed class RequestPerformanceMiddleware(
    RequestDelegate next,
    ILogger<RequestPerformanceMiddleware> logger)
{
    private const double SlowRequestThresholdMilliseconds = 1_000;

    public async Task InvokeAsync(
        HttpContext context,
        RequestPerformanceContext requestPerformance)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();

            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
                ?? "unmatched";
            var durationMs = stopwatch.Elapsed.TotalMilliseconds;
            var databaseDurationMs = requestPerformance.DatabaseDurationMilliseconds;
            var databaseCommandCount = requestPerformance.DatabaseCommandCount;
            var correlationId = context.Response.Headers["X-Correlation-ID"].FirstOrDefault()
                ?? context.TraceIdentifier;

            Telemetry.RequestDuration.Record(durationMs);
            Telemetry.DatabaseDurationPerRequest.Record(databaseDurationMs);
            Telemetry.DatabaseCommandsPerRequest.Record(databaseCommandCount);

            if (durationMs >= SlowRequestThresholdMilliseconds)
            {
                logger.LogWarning(
                    "Slow API request. Method={Method} Route={Route} StatusCode={StatusCode} DurationMs={DurationMs:F1} DatabaseDurationMs={DatabaseDurationMs:F1} DatabaseCommandCount={DatabaseCommandCount} CorrelationId={CorrelationId}",
                    context.Request.Method,
                    route,
                    context.Response.StatusCode,
                    durationMs,
                    databaseDurationMs,
                    databaseCommandCount,
                    correlationId);
            }
            else
            {
                logger.LogInformation(
                    "API request performance. Method={Method} Route={Route} StatusCode={StatusCode} DurationMs={DurationMs:F1} DatabaseDurationMs={DatabaseDurationMs:F1} DatabaseCommandCount={DatabaseCommandCount} CorrelationId={CorrelationId}",
                    context.Request.Method,
                    route,
                    context.Response.StatusCode,
                    durationMs,
                    databaseDurationMs,
                    databaseCommandCount,
                    correlationId);
            }
        }
    }
}
