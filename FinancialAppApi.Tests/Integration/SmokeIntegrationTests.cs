using System.Net;
using System.Net.Http.Json;

namespace FinancialAppApi.Tests.Integration;

public class SmokeIntegrationTests : IntegrationTestBase
{
    [Fact]
    public async Task Ping_ReturnsHealthy()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/api/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PingResponse>();
        Assert.Equal("healthy", body!.Status);
    }

    [Fact]
    public async Task ProtectedRoute_WithoutToken_Returns401()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/api/transactions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedRoute_WithSeededToken_Returns200()
    {
        var client = await CreateSignedInClientAsync();
        var response = await client.GetAsync("/api/transactions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record PingResponse(string Status);
}
