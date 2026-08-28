using System.Net;

namespace FinancialAppApi.Tests.Integration;

public sealed class SecurityHeadersIntegrationTests : IntegrationTestBase
{
    [Theory]
    [InlineData("/api/ping")]
    [InlineData("/api/auth/status")]
    [InlineData("/api/bootstrap")]
    [InlineData("/api/transactions/export")]
    [InlineData("/api/documents/1/content")]
    public async Task ApiResponsesCarryTheBaselineSecurityHeaders(string path)
    {
        var response = await CreateClient().GetAsync(path);

        Assert.NotEqual(HttpStatusCode.RequestTimeout, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("max-age=31536000; includeSubDomains", response.Headers.GetValues("Strict-Transport-Security").Single());
        Assert.Contains("camera=(self)", response.Headers.GetValues("Permissions-Policy").Single());
    }
}
