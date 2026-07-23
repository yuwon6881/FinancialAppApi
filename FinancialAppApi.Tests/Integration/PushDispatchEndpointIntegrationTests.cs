using System.Net;
using System.Net.Http.Headers;
using FinancialAppApi.Services.Push;
using Google.Apis.Auth;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FinancialAppApi.Tests.Integration;

/// <summary>
/// End-to-end coverage of POST /api/push/dispatch through the real middleware pipeline and the
/// AuthorizeGoogleOidc filter: no user session is ever accepted here — only a Google-signed OIDC
/// identity token, issued to an explicitly allowlisted service account, gets through.
/// </summary>
public class PushDispatchEndpointIntegrationTests : IntegrationTestBase
{
    private const string ServiceAccountEmail = "scheduler@my-project.iam.gserviceaccount.com";
    private const string Audience = "https://api.example.com/api/push/dispatch";

    [Fact]
    public async Task Dispatch_ReturnsUnauthorized_WhenNoBearerTokenIsPresent()
    {
        var client = CreateDispatchClient();

        var response = await client.PostAsync("/api/push/dispatch", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Dispatch_ReturnsUnauthorized_WhenTheAudienceIsNotConfigured()
    {
        var client = CreateDispatchClient(audience: "");
        AddBearerToken(client, "any-token");

        var response = await client.PostAsync("/api/push/dispatch", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Dispatch_ReturnsUnauthorized_WhenTheTokenFailsCryptographicVerification()
    {
        var client = CreateDispatchClient(verifier: new ThrowingVerifier());
        AddBearerToken(client, "forged-token");

        var response = await client.PostAsync("/api/push/dispatch", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Dispatch_ReturnsUnauthorized_WhenTheIssuerIsNotGoogle()
    {
        var payload = ValidPayload();
        payload.Issuer = "https://evil.example.com";
        var client = CreateDispatchClient(verifier: new StubVerifier(payload));
        AddBearerToken(client, "token");

        var response = await client.PostAsync("/api/push/dispatch", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Dispatch_ReturnsForbidden_WhenTheServiceAccountIsNotAllowlisted()
    {
        var client = CreateDispatchClient(
            verifier: new StubVerifier(ValidPayload()),
            allowlist: ["someone-else@my-project.iam.gserviceaccount.com"]);
        AddBearerToken(client, "token");

        var response = await client.PostAsync("/api/push/dispatch", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Dispatch_ReturnsForbidden_WhenTheAllowlistIsEmpty_EvenWithAnOtherwiseValidToken()
    {
        var client = CreateDispatchClient(verifier: new StubVerifier(ValidPayload()), allowlist: []);
        AddBearerToken(client, "token");

        var response = await client.PostAsync("/api/push/dispatch", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Dispatch_Succeeds_ForAValidTokenFromAnAllowlistedServiceAccount()
    {
        var client = CreateDispatchClient(verifier: new StubVerifier(ValidPayload()));
        AddBearerToken(client, "token");

        var response = await client.PostAsync("/api/push/dispatch", null);

        response.EnsureSuccessStatusCode();
    }

    private HttpClient CreateDispatchClient(
        string audience = Audience,
        string[]? allowlist = null,
        IGoogleIdTokenVerifier? verifier = null)
    {
        allowlist ??= [ServiceAccountEmail];
        var factory = Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Push:OidcAudience", audience);
            for (var i = 0; i < allowlist.Length; i++)
            {
                builder.UseSetting($"Push:AllowlistedServiceAccounts:{i}", allowlist[i]);
            }

            if (verifier != null)
            {
                builder.ConfigureTestServices(services => services.AddSingleton(verifier));
            }
        });
        return factory.CreateClient();
    }

    private static void AddBearerToken(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static GoogleJsonWebSignature.Payload ValidPayload() => new()
    {
        Issuer = "https://accounts.google.com",
        Audience = Audience,
        Email = ServiceAccountEmail,
        EmailVerified = true
    };

    private sealed class StubVerifier(GoogleJsonWebSignature.Payload payload) : IGoogleIdTokenVerifier
    {
        public Task<GoogleJsonWebSignature.Payload> VerifyAsync(string idToken, string audience, CancellationToken cancellationToken = default)
            => Task.FromResult(payload);
    }

    private sealed class ThrowingVerifier : IGoogleIdTokenVerifier
    {
        public Task<GoogleJsonWebSignature.Payload> VerifyAsync(string idToken, string audience, CancellationToken cancellationToken = default)
            => throw new InvalidJwtException("Signature verification failed.");
    }
}
