using System.Security.Cryptography;

namespace FinancialAppApi.Services;

/// <summary>
/// Single source of truth for the browser session cookies. Browser clients authenticate with the
/// HttpOnly <c>auth_token</c> cookie; a companion non-HttpOnly <c>csrf_token</c> cookie backs the
/// double-submit CSRF defence (see <c>CsrfProtectionMiddleware</c>). Native clients ignore cookies
/// and keep using the <c>Authorization: Bearer</c> header, so they receive no cookies here.
/// </summary>
public sealed class AuthCookieService
{
    public const string AuthCookieName = "auth_token";
    public const string CsrfCookieName = "csrf_token";
    public const string CsrfHeaderName = "X-CSRF-Token";

    // Matches the session's DB ExpiresAt (AuthSessionService uses +7 days) so the cookie does not
    // linger long after the session it points at has been pruned.
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Issues the auth + CSRF cookies for a freshly established session and returns the CSRF token.
    /// </summary>
    public string IssueSessionCookies(HttpResponse response, string token)
    {
        var csrfToken = GenerateCsrfToken();
        var expires = DateTimeOffset.UtcNow.Add(CookieLifetime);
        response.Cookies.Append(AuthCookieName, token, BuildAuthCookieOptions(expires));
        response.Cookies.Append(CsrfCookieName, csrfToken, BuildCsrfCookieOptions(expires));
        response.Headers[CsrfHeaderName] = csrfToken;
        return csrfToken;
    }

    /// <summary>Rotates the CSRF token for an existing cookie-authenticated browser session.</summary>
    public string RefreshCsrfCookie(HttpResponse response)
    {
        var csrfToken = GenerateCsrfToken();
        response.Cookies.Append(CsrfCookieName, csrfToken, BuildCsrfCookieOptions(DateTimeOffset.UtcNow.Add(CookieLifetime)));
        response.Headers[CsrfHeaderName] = csrfToken;
        return csrfToken;
    }

    /// <summary>Clears both session cookies (logout / session teardown).</summary>
    public void ClearSessionCookies(HttpResponse response)
    {
        response.Cookies.Delete(AuthCookieName, BuildAuthCookieOptions(null));
        response.Cookies.Delete(CsrfCookieName, BuildCsrfCookieOptions(null));
    }

    // Cross-site (Vercel frontend -> Cloud Run API) requires SameSite=None + Secure so the cookie is
    // sent at all; that also means SameSite cannot defend against CSRF, hence the double-submit token.
    private static CookieOptions BuildAuthCookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.None,
        Path = "/",
        Expires = expires,
    };

    // Not HttpOnly: the SPA must read this value to echo it back in the X-CSRF-Token header.
    private static CookieOptions BuildCsrfCookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = false,
        Secure = true,
        SameSite = SameSiteMode.None,
        Path = "/",
        Expires = expires,
    };

    private static string GenerateCsrfToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
