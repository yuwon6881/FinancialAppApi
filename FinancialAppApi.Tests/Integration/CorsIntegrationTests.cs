namespace FinancialAppApi.Tests.Integration;

/// <summary>
/// Verifies the CORS policy backing cookie auth: the configured frontend origin is reflected with
/// credentials enabled, while an unknown origin receives no Access-Control-Allow-Origin header.
/// </summary>
public class CorsIntegrationTests : IntegrationTestBase
{
    // Matches Cors:AllowedOrigins in appsettings.json (used in the Testing environment).
    private const string AllowedOrigin = "https://financialapp-ecru.vercel.app";

    [Fact]
    public async Task ConfiguredOrigin_IsReflectedWithCredentials()
    {
        var client = CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/status");
        request.Headers.TryAddWithoutValidation("Origin", AllowedOrigin);

        var response = await client.SendAsync(request);

        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origin));
        Assert.Equal(AllowedOrigin, origin!.Single());
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Credentials", out var creds));
        Assert.Equal("true", creds!.Single());
    }

    [Fact]
    public async Task UnknownOrigin_IsNotAllowed()
    {
        var client = CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/status");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");

        var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_FromConfiguredOrigin_AllowsCredentialedMutation()
    {
        var client = CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/auth/lock");
        request.Headers.TryAddWithoutValidation("Origin", AllowedOrigin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", AllowedHeaders());

        var response = await client.SendAsync(request);

        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origin));
        Assert.Equal(AllowedOrigin, origin!.Single());
    }

    private static string AllowedHeaders() => "x-csrf-token,content-type";
}
