using System.Security.Cryptography;
using System.Text;
using FinancialAppApi.Database;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;

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
    private static readonly HashSet<string> AnonymousAuthPosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth/login", "/api/auth/login/2fa", "/api/auth/register",
        "/api/auth/security-questions/recovery/start", "/api/auth/security-questions/recovery/reset",
    };

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
                // A revoked or expired cookie conveys no authentication. Permit the same public
                // recovery routes available without cookies while keeping CSRF for a live session.
                if (HttpMethods.IsPost(request.Method) && AnonymousAuthPosts.Contains(request.Path.Value?.TrimEnd('/') ?? ""))
                {
                    var db = context.RequestServices.GetRequiredService<AppDbContext>();
                    var hasLiveSession = await db.UserSessions.AsNoTracking().AnyAsync(
                        session => session.Token == authCookie && session.ExpiresAt > DateTime.UtcNow,
                        context.RequestAborted);
                    if (!hasLiveSession)
                    {
                        await _next(context);
                        return;
                    }
                }
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { message = "CSRF validation failed" });
                return;
            }
        }

        await _next(context);
    }
}
