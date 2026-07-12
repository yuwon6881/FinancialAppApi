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

        if (request.Headers.TryGetValue("Authorization", out var authHeaderValues))
        {
            var authHeader = authHeaderValues.ToString();
            if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return BearerTokenResult.Malformed;
            }

            token = authHeader.Substring("Bearer ".Length).Trim();
            return BearerTokenResult.Ok;
        }

        if (request.Cookies.TryGetValue("auth_token", out var cookieValue))
        {
            if (!string.IsNullOrWhiteSpace(cookieValue))
            {
                token = cookieValue.Trim();
                return BearerTokenResult.Ok;
            }
        }

        return BearerTokenResult.Missing;
    }
}
