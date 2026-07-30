using FinancialAppApi.Diagnostics;
using FinancialAppApi.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;

namespace FinancialAppApi.Tests;

public sealed class RequestPerformanceMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_LogsTheRouteTemplateAndAggregateDatabaseWork()
    {
        var logger = new CapturingLogger<RequestPerformanceMiddleware>();
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/transactions/private-record-id";
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/transactions/{id}"),
            order: 0,
            EndpointMetadataCollection.Empty,
            displayName: "transaction"));
        var performance = new RequestPerformanceContext();
        performance.RecordDatabaseCommand(TimeSpan.FromMilliseconds(12));
        performance.RecordDatabaseCommand(TimeSpan.FromMilliseconds(8));
        var middleware = new RequestPerformanceMiddleware(
            next: request =>
            {
                request.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            logger);

        await middleware.InvokeAsync(context, performance);

        Assert.Contains("Route=/api/transactions/{id}", logger.Messages.Single());
        Assert.Contains("DatabaseCommandCount=2", logger.Messages.Single());
        Assert.DoesNotContain("private-record-id", logger.Messages.Single());
    }

    [Fact]
    public async Task InvokeAsync_DoesNotLogAnUnmatchedRawPath()
    {
        var logger = new CapturingLogger<RequestPerformanceMiddleware>();
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/documents/private-filename.pdf";
        var middleware = new RequestPerformanceMiddleware(
            next: _ => Task.CompletedTask,
            logger);

        await middleware.InvokeAsync(context, new RequestPerformanceContext());

        Assert.Contains("Route=unmatched", logger.Messages.Single());
        Assert.DoesNotContain("private-filename.pdf", logger.Messages.Single());
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
