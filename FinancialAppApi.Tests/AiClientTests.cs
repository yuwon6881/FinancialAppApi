using System.Net;
using System.Text;
using System.Text.Json;
using FinancialAppApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class AiClientTests
{
    [Fact]
    public async Task GenerateTextAsync_SendsStructuredSchemaThinkingAndHeaderKey()
    {
        var handler = new RecordingHandler(_ => SuccessResponse("{\"ok\":true}"));
        var client = new AiClient(
            new HttpClient(handler),
            TestHelpers.NewConfiguration(("AiApiKey", "secret-key"), ("AiModel", "test-model")),
            NullLogger<AiClient>.Instance);

        var result = await client.GenerateTextAsync(
            [AiPart.FromText("input")],
            new AiGenerationOptions(
                "test-feature",
                0,
                123,
                "system rules",
                new { type = "object", properties = new { ok = new { type = "boolean" } } },
                "low"));

        Assert.Equal("{\"ok\":true}", result);
        Assert.NotNull(handler.LastRequestUri);
        Assert.DoesNotContain("secret-key", handler.LastRequestUri!.ToString());
        Assert.Equal("secret-key", handler.ApiKey);

        using var body = JsonDocument.Parse(handler.Body!);
        var config = body.RootElement.GetProperty("generationConfig");
        Assert.Equal("application/json", config.GetProperty("responseMimeType").GetString());
        Assert.Equal("object", config.GetProperty("responseJsonSchema").GetProperty("type").GetString());
        Assert.Equal("low", config.GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
        Assert.Equal(123, config.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal("system rules", body.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void ChatSchema_KeepsLedgerDraftPayloadFlat()
    {
        var schema = JsonSerializer.SerializeToElement(AiResponseSchemas.Chat(["Food", "Transport"]));
        var actions = schema.GetProperty("properties").GetProperty("actions");
        var payloadProperties = actions.GetProperty("items")
            .GetProperty("properties")
            .GetProperty("payload")
            .GetProperty("properties");

        Assert.Equal(AiResponseSchemas.MaxChatActions, actions.GetProperty("maxItems").GetInt32());
        Assert.False(payloadProperties.TryGetProperty("transactions", out _));
        Assert.Equal("boolean", payloadProperties.GetProperty("ledgerCategorySpecified").GetProperty("type").GetString());
    }

    [Fact]
    public void LedgerDraftChatSchema_RequiresTwoCompleteFlatActionsForTwoLineInput()
    {
        var schema = JsonSerializer.SerializeToElement(AiResponseSchemas.LedgerDraftChat(["Food", "Bills"], 2));
        var actions = schema.GetProperty("properties").GetProperty("actions");
        var actionItem = actions.GetProperty("items");
        var payload = actionItem.GetProperty("properties").GetProperty("payload");
        var required = payload.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToList();

        Assert.Equal(2, actions.GetProperty("minItems").GetInt32());
        Assert.Equal(2, actions.GetProperty("maxItems").GetInt32());
        Assert.Equal(["openAddLedgerDraft"], actionItem.GetProperty("properties").GetProperty("type").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("description", required);
        Assert.Contains("amount", required);
        Assert.Contains("category", required);
        Assert.Contains("ledgerCategorySpecified", required);
        Assert.False(payload.GetProperty("properties").TryGetProperty("transactions", out _));
    }

    [Fact]
    public async Task GenerateTextAsync_RetriesTransientFailureOnce()
    {
        var attempt = 0;
        var handler = new RecordingHandler(_ =>
        {
            attempt++;
            return attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : SuccessResponse("done");
        });
        var client = new AiClient(
            new HttpClient(handler),
            TestHelpers.NewConfiguration(("AiApiKey", "key"), ("AiModel", "same-model")),
            NullLogger<AiClient>.Instance);

        var result = await client.GenerateTextAsync(
            [AiPart.FromText("input")],
            new AiGenerationOptions("retry-test", 0, 20));

        Assert.Equal("done", result);
        Assert.Equal(2, attempt);
    }

    private static HttpResponseMessage SuccessResponse(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""
            {
              "candidates": [{ "content": { "parts": [{ "text": {{JsonSerializer.Serialize(text)}} }] }, "finishReason": "STOP" }],
              "usageMetadata": { "promptTokenCount": 4, "candidatesTokenCount": 2 }
            }
            """,
            Encoding.UTF8,
            "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public string? ApiKey { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            ApiKey = request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.Single() : null;
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}
