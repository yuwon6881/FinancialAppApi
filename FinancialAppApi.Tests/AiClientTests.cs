using System.Net;
using System.Text;
using System.Text.Json;
using FinancialAppApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class AiClientTests
{
    [Fact]
    public void ReceiptSplitSchema_DoesNotSendLargeArrayGrammarLimitsToLiteModel()
    {
        var json = JsonSerializer.Serialize(AiResponseSchemas.ReceiptSplit(["Food", "Other"]));

        Assert.DoesNotContain("\"maxItems\"", json, StringComparison.Ordinal);
        using var schema = JsonDocument.Parse(json);
        var quantity = schema.RootElement
            .GetProperty("properties")
            .GetProperty("items")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("quantity");
        Assert.Equal("integer", quantity.GetProperty("type").GetString());
    }

    [Fact]
    public async Task GenerateTextAsync_SendsOpenAiStructuredSchemaReasoningAndBearerToken()
    {
        var handler = new RecordingHandler(_ => SuccessResponse("{\"ok\":true}"));
        var client = new AiClient(
            new HttpClient(handler),
            TestHelpers.NewConfiguration(("OpenAiApiKey", "secret-key"), ("OpenAiModel", "test-model")),
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
        var format = body.RootElement.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("object", format.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("low", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(123, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("system rules", body.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal("input_text", body.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("type").GetString());
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
            TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "same-model")),
            NullLogger<AiClient>.Instance);

        var result = await client.GenerateTextAsync(
            [AiPart.FromText("input")],
            new AiGenerationOptions("retry-test", 0, 20));

        Assert.Equal("done", result);
        Assert.Equal(2, attempt);
    }

    [Fact]
    public async Task GenerateTextAsync_SendsInlineImageAsOpenAiDataUrl()
    {
        var handler = new RecordingHandler(_ => SuccessResponse("{\"ok\":true}"));
        var client = new AiClient(
            new HttpClient(handler),
            TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "vision-model")),
            NullLogger<AiClient>.Instance);

        await client.GenerateTextAsync(
            [AiPart.FromText("Read this receipt."), AiPart.FromImage("image/jpeg", "AQID")],
            new AiGenerationOptions("receipt-ocr", 0, 100));

        using var body = JsonDocument.Parse(handler.Body!);
        var image = body.RootElement.GetProperty("input")[0].GetProperty("content")[1];
        Assert.Equal("input_image", image.GetProperty("type").GetString());
        Assert.Equal("data:image/jpeg;base64,AQID", image.GetProperty("image_url").GetString());
        Assert.Equal("auto", image.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task GenerateTextAsync_SendsPdfAsBase64InputFile()
    {
        var handler = new RecordingHandler(_ => SuccessResponse("{\"amount\":12.5}"));
        var client = new AiClient(
            new HttpClient(handler),
            TestHelpers.NewConfiguration(("OpenAiApiKey", "key"), ("OpenAiModel", "vision-model")),
            NullLogger<AiClient>.Instance);

        await client.GenerateTextAsync(
            [AiPart.FromFile("receipt.pdf", "application/pdf", "JVBERg=="), AiPart.FromText("Read total.")],
            new AiGenerationOptions("vault-amount-extraction", 0, 100));

        using var body = JsonDocument.Parse(handler.Body!);
        var file = body.RootElement.GetProperty("input")[0].GetProperty("content")[0];
        Assert.Equal("input_file", file.GetProperty("type").GetString());
        Assert.Equal("receipt.pdf", file.GetProperty("filename").GetString());
        Assert.Equal("data:application/pdf;base64,JVBERg==", file.GetProperty("file_data").GetString());
        Assert.Equal("low", file.GetProperty("detail").GetString());
    }

    private static HttpResponseMessage SuccessResponse(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""
            {
              "status": "completed",
              "output": [{ "type": "message", "content": [{ "type": "output_text", "text": {{JsonSerializer.Serialize(text)}} }] }],
              "usage": { "input_tokens": 4, "output_tokens": 2 }
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
            ApiKey = request.Headers.Authorization?.Scheme == "Bearer"
                ? request.Headers.Authorization.Parameter
                : null;
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}
