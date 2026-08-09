using FinancialAppApi.Database;
using FinancialAppApi.Services.Push;

namespace FinancialAppApi.Middleware;

// Runs after the response has been produced, so FCM latency never delays a ledger mutation. The
// durable evaluations were committed with the transaction and the scheduled push dispatcher is a
// recovery path if this callback is interrupted by instance shutdown.
public sealed class CategoryLimitAlertMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CategoryLimitAlertMiddleware> _logger;

    public CategoryLimitAlertMiddleware(
        RequestDelegate next,
        IServiceScopeFactory scopeFactory,
        ILogger<CategoryLimitAlertMiddleware> logger)
    {
        _next = next;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Resolved here, while the request scope is certainly alive, so the callback can read the
        // flag without touching request services after completion. Instantiating a DbContext does
        // not open a connection, and every request this middleware can act on resolves one anyway.
        var requestDbContext = context.RequestServices.GetRequiredService<AppDbContext>();

        context.Response.OnCompleted(async () =>
        {
            if (!context.Items.TryGetValue("UserId", out var value) || value is not string userId)
            {
                return;
            }

            // Nothing this request wrote can have produced a crossing. An event left incomplete by
            // an earlier interrupted request is not chased here -- the scheduled dispatch is the
            // documented recovery path, and querying for one on every response is what this skip
            // exists to avoid.
            if (!requestDbContext.HasCapturedCategoryLimitEvaluations)
            {
                return;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                dbContext.SetCurrentUser(userId);
                var processor = scope.ServiceProvider.GetRequiredService<CategoryLimitAlertProcessor>();
                await processor.ProcessPendingAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Category limit alert processing will be retried by scheduled push dispatch.");
            }
        });

        await _next(context);
    }
}
