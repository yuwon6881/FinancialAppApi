using System.Text;
using FinancialAppApi.Middleware;
using Microsoft.AspNetCore.Http;

namespace FinancialAppApi.Tests;

public sealed class ConditionalGetMiddlewareTests
{
    [Theory]
    [InlineData("/api/documents/12/content")]
    [InlineData("/api/documents/export")]
    public async Task StreamingDocumentRoutes_KeepTheOriginalResponseStream(string path)
    {
        var original = new MemoryStream();
        var sawOriginalStream = false;
        var middleware = new ConditionalGetMiddleware(async context =>
        {
            sawOriginalStream = ReferenceEquals(context.Response.Body, original);
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("streamed"));
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Response.Body = original;

        await middleware.InvokeAsync(context);

        Assert.True(sawOriginalStream);
        Assert.Equal("streamed", Encoding.UTF8.GetString(original.ToArray()));
    }
}
