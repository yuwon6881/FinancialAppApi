namespace FinancialAppApi.Extensions;

public static class ClientPlatformExtensions
{
    private const string ClientHeader = "X-FinancialApp-Client";

    public static bool IsNativeClient(this HttpRequest request) =>
        string.Equals(request.Headers[ClientHeader], "native", StringComparison.OrdinalIgnoreCase);
}
