using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FinancialAppApi.Tests.Integration;

public class AiChatStreamIntegrationTests : IntegrationTestBase
{
    [Fact]
    public async Task StreamEndsWithADoneEventCarryingTheSameResponseShapeAsTheJsonEndpoint()
    {
        var client = await CreateSignedInClientAsync();

        var response = await client.PostAsJsonAsync("/api/ai/chat/stream", new { message = "How much did I spend this cycle?" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var events = Parse(await response.Content.ReadAsStringAsync());
        var (name, data) = Assert.Single(events);
        Assert.Equal("done", name);
        Assert.Equal("Context received.", data.GetProperty("reply").GetString());
        Assert.Equal(JsonValueKind.Array, data.GetProperty("actions").ValueKind);
    }

    [Fact]
    public async Task TheModelIsOfferedTheReadToolsAndTheBaselineSnapshot()
    {
        var client = await CreateSignedInClientAsync();

        var response = await client.PostAsJsonAsync("/api/ai/chat/stream", new { message = "Am I doing okay this month?" });

        var events = Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("done", events[^1].Name);
        Assert.Contains("search_transactions", Factory.LastAiRequestBody);
        Assert.Contains("propose_ui_actions", Factory.LastAiRequestBody);
        Assert.Contains("snapshot", Factory.LastAiRequestBody);
    }

    [Fact]
    public async Task UnauthenticatedStreamIsRejectedBeforeAnyEventIsWritten()
    {
        var response = await CreateClient().PostAsJsonAsync("/api/ai/chat/stream", new { message = "hi" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static List<(string Name, JsonElement Data)> Parse(string body) =>
        body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(block => !block.StartsWith(':'))
            .Select(block =>
            {
                var lines = block.Split('\n');
                var name = lines.First(line => line.StartsWith("event: ")).Substring("event: ".Length);
                var data = lines.First(line => line.StartsWith("data: ")).Substring("data: ".Length);
                return (name, JsonDocument.Parse(data).RootElement.Clone());
            })
            .ToList();
}
