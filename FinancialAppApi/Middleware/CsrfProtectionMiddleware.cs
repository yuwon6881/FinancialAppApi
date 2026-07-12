using System.Security.Cryptography;
using System.Text;
using FinancialAppApi.Services;

namespace FinancialAppApi.Middleware;

/// <summary>
/// Double-submit-cookie CSRF protection for cookie-authenticated (browser) requests.
///
/// The check applies only when the request carries the <c>auth_token</c> cookie and uses an unsafe
/// HTTP method. Native clients authenticate with the <c>Authorization</c> header and send no cookies,
/// so an attacker cannot forge their requests cross-site — those are skipped. For browser requests the
/// non-HttpOnly <c>csrf_token</c> cookie must match the <c>X-CSRF-Token</c> header (which only same-origin
/// JS can read and set), so a cross-site forgery cannot supply the header.
/// </summary>
public sealed class CsrfProtectionMiddleware
{
    private readonly RequestDelegate _next;

    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };

    public CsrfProtectionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;

        var hasAuthCookie = request.Cookies.TryGetValue(AuthCookieService.AuthCookieName, out var authCookie)
            && !string.IsNullOrWhiteSpace(authCookie);

        if (hasAuthCookie && !SafeMethods.Contains(request.Method))
        {
            var cookieToken = request.Cookies[AuthCookieService.CsrfCookieName];
            var headerToken = request.Headers[AuthCookieService.CsrfHeaderName].ToString();

            if (string.IsNullOrEmpty(cookieToken)
                || string.IsNullOrEmpty(headerToken)
                || !CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(cookieToken),
                        Encoding.UTF8.GetBytes(headerToken)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { message = "CSRF validation failed" });
                return;
            }
        }

        await _next(context);
    }
}
