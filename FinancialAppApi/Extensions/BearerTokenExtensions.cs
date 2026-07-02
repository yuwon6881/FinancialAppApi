namespace FinancialAppApi.Extensions;

public enum BearerTokenResult
{
    Missing,
    Malformed,
    Ok
}

public static class BearerTokenExtensions
{
    public static BearerTokenResult TryGetBearerToken(this HttpRequest request, out string token)
    {
        token = string.Empty;

        if (!request.Headers.TryGetValue("Authorization", out var authHeaderValues))
        {
            return BearerTokenResult.Missing;
        }

        var authHeader = authHeaderValues.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return BearerTokenResult.Malformed;
        }

        token = authHeader.Substring("Bearer ".Length).Trim();
        return BearerTokenResult.Ok;
    }
}
