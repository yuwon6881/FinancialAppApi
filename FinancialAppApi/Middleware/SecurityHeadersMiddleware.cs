using Microsoft.Net.Http.Headers;

namespace FinancialAppApi.Middleware;

/// <summary>
/// Applies the response protections that Cloud Run cannot add after MVC has produced a response.
/// The web shell's CSP is owned by Vercel; API responses deliberately do not send a script policy
/// so document bytes, CSV exports, and streamed responses retain their existing semantics.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers[HeaderNames.StrictTransportSecurity] = "max-age=31536000; includeSubDomains";
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers[HeaderNames.XFrameOptions] = "DENY";
        headers["Permissions-Policy"] =
            "camera=(self), clipboard-write=(self), geolocation=(), microphone=(), payment=()";

        await next(context);
    }
}
