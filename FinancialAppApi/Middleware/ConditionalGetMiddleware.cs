using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;

namespace FinancialAppApi.Middleware;

/// <summary>
/// Adds weak ETags to API GET responses and answers a matching If-None-Match with 304.
/// </summary>
/// <remarks>
/// The app polls the dashboard and re-reads its slices after every sync. On a phone with a
/// poor connection those responses were re-downloaded and re-parsed in full even when nothing
/// had changed. A 304 skips both the transfer and the client-side JSON parse, which is the part
/// the user actually feels.
///
/// The ETag is computed over the response body, so the work still happens server-side — this
/// trades a little CPU (one SHA-256 over a payload already in memory) for bandwidth and client
/// parse time. That is the right trade here: bytes over a mobile link cost more than hashing.
///
/// Caching rules, deliberately conservative because every payload is per-user financial data:
/// <list type="bullet">
/// <item>`Cache-Control: private, no-cache` — never a shared cache, and always revalidate.
/// `no-cache` (not `no-store`) is what permits the 304 round trip at all; `no-store` would
/// forbid keeping the copy that makes revalidation possible.</item>
/// <item>Only 200 GET responses under /api are considered.</item>
/// <item>Anything carrying Set-Cookie is skipped: an ETag'd 304 would drop the cookie, so
/// login/refresh responses must never be revalidated.</item>
/// <item>Responses that already set Cache-Control or ETag are left alone.</item>
/// </list>
/// </remarks>
public sealed class ConditionalGetMiddleware
{
    private readonly RequestDelegate _next;

    // Above this the buffering cost outweighs the saving; such payloads are rare here
    // (the largest, /api/bootstrap, is far below it) and stream through untouched.
    private const int MaxBufferedBytes = 4 * 1024 * 1024;

    public ConditionalGetMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsCandidate(context))
        {
            await _next(context);
            return;
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);

            // Re-check after the response is produced: status, headers and Set-Cookie are
            // only known now.
            if (!IsCacheableResponse(context) || buffer.Length == 0 || buffer.Length > MaxBufferedBytes)
            {
                await CopyToAsync(buffer, originalBody, context.RequestAborted);
                return;
            }

            var etag = ComputeWeakETag(buffer);
            context.Response.Headers[HeaderNames.ETag] = etag;
            context.Response.Headers[HeaderNames.CacheControl] = "private, no-cache";

            if (RequestMatchesETag(context.Request, etag))
            {
                // 304 must carry no body. Restore the real stream first so nothing is written
                // to the buffer that is about to be dropped, and clear Content-Length so it
                // cannot describe a body that is no longer being sent.
                context.Response.Body = originalBody;
                context.Response.StatusCode = StatusCodes.Status304NotModified;
                context.Response.ContentLength = null;
                return;
            }

            // Content-Length is deliberately left alone. This middleware runs inside response
            // compression, so `buffer.Length` is the *uncompressed* size — advertising it would
            // contradict the compressed bytes actually written.
            await CopyToAsync(buffer, originalBody, context.RequestAborted);
        }
        finally
        {
            // Always hand the real stream back, including on an exception, so the error
            // response is not written into a buffer that is about to be dropped.
            context.Response.Body = originalBody;
        }
    }

    private static async Task CopyToAsync(MemoryStream buffer, Stream destination, CancellationToken cancellationToken)
    {
        buffer.Position = 0;
        await buffer.CopyToAsync(destination, cancellationToken);
    }

    private static bool IsCandidate(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method)) return false;
        // This endpoint streams the ledger row-by-row. Buffering it here would recreate the
        // full-history memory spike the streaming implementation is intended to avoid.
        if (context.Request.Path.Equals(
                "/api/transactions/export",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var path = context.Request.Path.Value;
        if (path != null &&
            (path.Equals("/api/documents/export", StringComparison.OrdinalIgnoreCase)
             || (path.StartsWith("/api/documents/", StringComparison.OrdinalIgnoreCase)
                 && path.EndsWith("/content", StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }
        return context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCacheableResponse(HttpContext context)
    {
        var response = context.Response;
        if (response.StatusCode != StatusCodes.Status200OK) return false;
        // A 304 would discard the cookie the client needs.
        if (response.Headers.ContainsKey(HeaderNames.SetCookie)) return false;
        // Respect anything an endpoint has decided for itself.
        if (response.Headers.ContainsKey(HeaderNames.CacheControl)) return false;
        if (response.Headers.ContainsKey(HeaderNames.ETag)) return false;
        return true;
    }

    private static string ComputeWeakETag(MemoryStream buffer)
    {
        // Weak (W/): the tag identifies semantically equivalent content, and the body may
        // still be re-encoded downstream (response compression runs outside this middleware).
        var hash = SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        return $"W/\"{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}\"";
    }

    private static bool RequestMatchesETag(HttpRequest request, string etag)
    {
        var header = request.Headers[HeaderNames.IfNoneMatch];
        if (header.Count == 0) return false;

        foreach (var value in header)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            // A client may send several tags, and "*" matches any existing representation.
            foreach (var candidate in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (candidate == "*") return true;
                // Compare ignoring the weak marker, per RFC 9110's weak comparison.
                if (StripWeakPrefix(candidate) == StripWeakPrefix(etag)) return true;
            }
        }
        return false;
    }

    private static string StripWeakPrefix(string etag) =>
        etag.StartsWith("W/", StringComparison.Ordinal) ? etag[2..] : etag;
}
