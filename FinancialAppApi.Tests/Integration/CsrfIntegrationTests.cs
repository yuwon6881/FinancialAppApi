using System.Net;
using System.Net.Http.Json;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests.Integration;

/// <summary>
/// Exercises the double-submit-cookie CSRF middleware and the cookie/bearer transport split through
/// the real HTTP pipeline: cookie-authenticated mutations require a matching X-CSRF-Token header,
/// native bearer clients are exempt, safe methods are never challenged, and login issues both cookies.
/// </summary>
public class CsrfIntegrationTests : IntegrationTestBase
{
    private const string CsrfHeader = AuthCookieService.CsrfHeaderName;

    private static HttpRequestMessage Post(string path, string cookieHeader, string? csrfHeader = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        if (csrfHeader is not null) request.Headers.TryAddWithoutValidation(CsrfHeader, csrfHeader);
        return request;
    }

    [Fact]
    public async Task CookieAuthedMutation_WithMatchingCsrf_PassesCsrfCheck()
    {
        var token = await SeedUserAndSessionAsync();
        var client = CreateClient();

        var response = await client.SendAsync(
            Post("/api/auth/lock", $"auth_token={token}; csrf_token=abc123", csrfHeader: "abc123"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CookieAuthedMutation_WithoutCsrfHeader_IsForbidden()
    {
        var token = await SeedUserAndSessionAsync();
        var client = CreateClient();

        var response = await client.SendAsync(
            Post("/api/auth/lock", $"auth_token={token}; csrf_token=abc123"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CookieAuthedMutation_WithMismatchedCsrf_IsForbidden()
    {
        var token = await SeedUserAndSessionAsync();
        var client = CreateClient();

        var response = await client.SendAsync(
            Post("/api/auth/lock", $"auth_token={token}; csrf_token=abc123", csrfHeader: "different"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CookieAuthedSafeRequest_WithoutCsrf_IsAllowed()
    {
        var token = await SeedUserAndSessionAsync();
        var client = CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/transactions");
        request.Headers.TryAddWithoutValidation("Cookie", $"auth_token={token}; csrf_token=abc123");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NativeBearerMutation_WithoutCsrf_IsAllowed()
    {
        // Native clients send no cookies and cannot be CSRF'd, so they must not be challenged.
        var token = await SeedUserAndSessionAsync();
        var client = CreateAuthenticatedClient(token);

        var response = await client.PostAsync("/api/auth/lock", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Logout_WithCookieButNoCsrf_IsForbidden()
    {
        var token = await SeedUserAndSessionAsync();
        var client = CreateClient();

        var response = await client.SendAsync(
            Post("/api/auth/logout", $"auth_token={token}; csrf_token=abc123"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Logout_WithMatchingCsrf_Succeeds()
    {
        var token = await SeedUserAndSessionAsync();
        var client = CreateClient();

        var response = await client.SendAsync(
            Post("/api/auth/logout", $"auth_token={token}; csrf_token=abc123", csrfHeader: "abc123"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_IssuesHttpOnlyAuthCookieAndReadableCsrfCookie()
    {
        var client = CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new { username = "alice", password = "Password123!" });

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "alice", password = "Password123!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var setCookies = login.Headers.GetValues("Set-Cookie").ToList();
        var authCookie = Assert.Single(setCookies, c => c.StartsWith("auth_token="));
        var csrfCookie = Assert.Single(setCookies, c => c.StartsWith("csrf_token="));

        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", authCookie, StringComparison.OrdinalIgnoreCase);
        // The CSRF cookie must be readable by JS (no HttpOnly) so the SPA can echo it.
        Assert.DoesNotContain("httponly", csrfCookie, StringComparison.OrdinalIgnoreCase);
        Assert.True(login.Headers.Contains(AuthCookieService.CsrfHeaderName));
    }

    [Fact]
    public async Task CookieAuthenticatedCsrfBootstrap_RotatesAndExposesToken()
    {
        var token = await SeedUserAndSessionAsync();
        var client = CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/csrf");
        request.Headers.TryAddWithoutValidation("Cookie", $"auth_token={token}; csrf_token=old");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.Contains(AuthCookieService.CsrfHeaderName));
        Assert.Contains(response.Headers.GetValues(AuthCookieService.CsrfHeaderName), value => value.Length >= 32);
    }

    [Fact]
    public async Task NativeLogin_DoesNotIssueBrowserCookies()
    {
        var client = CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new { username = "native-user", password = "Password123!" });

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { username = "native-user", password = "Password123!" })
        };
        request.Headers.TryAddWithoutValidation("X-FinancialApp-Client", "native");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Login_LogsOutBearer_ClearsCookies()
    {
        var token = await SeedUserAndSessionAsync();
        var client = CreateAuthenticatedClient(token);

        var response = await client.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var setCookies = response.Headers.GetValues("Set-Cookie").ToList();
        // Deleting a cookie emits an expired Set-Cookie for it.
        Assert.Contains(setCookies, c => c.StartsWith("auth_token="));
        Assert.Contains(setCookies, c => c.StartsWith("csrf_token="));
    }
}
