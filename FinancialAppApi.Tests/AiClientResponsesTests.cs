using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FinancialAppApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class AiClientResponsesTests
{
    private static readonly JsonObject EmptyParameters = new() { ["type"] = "object", ["properties"] = new JsonObject() };

    [Fact]
    public async Task CreateResponseAsync_SendsToolsChoiceReasoningInclusionAndCacheKey()
    {
        var handler = new QueueHandler(_ => Json(CompletedResponse(Message("hi"))));
        var client = NewClient(handler);

        await client.CreateResponseAsync(new AiResponseRequest(
            "chat",
            [AiInputItems.Message("user", "hello"), AiInputItems.FunctionCallOutput("call_1", "{\"ok\":true}")],
            500,
            "OpenAiModels:Chat",
            Instructions: "rules",
            Tools: [new AiFunctionTool("search_transactions", "Search.", EmptyParameters)],
            ToolChoice: "search_transactions",
            PromptCacheKey: "ask-ai-v3"));

        using var body = JsonDocument.Parse(handler.Bodies.Single());
        var root = body.RootElement;
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal("rules", root.GetProperty("instructions").GetString());
        Assert.Equal("low", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("reasoning.encrypted_content", root.GetProperty("include")[0].GetString());
        Assert.Equal("ask-ai-v3", root.GetProperty("prompt_cache_key").GetString());
        Assert.True(root.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.False(root.TryGetProperty("stream", out _));
        var tool = root.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("search_transactions", tool.GetProperty("name").GetString());
        var choice = root.GetProperty("tool_choice");
        Assert.Equal("function", choice.GetProperty("type").GetString());
        Assert.Equal("search_transactions", choice.GetProperty("name").GetString());
        Assert.Equal("function_call_output", root.GetProperty("input")[1].GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("none")]
    [InlineData("required")]
    public void BuildResponsesRequestBody_SendsModeToolChoicesAsPlainStrings(string choice)
    {
        var bytes = AiClient.BuildResponsesRequestBody(
            new AiResponseRequest("chat", [AiInputItems.Message("user", "x")], 10, "k", ToolChoice: choice),
            "m",
            stream: true);

        using var body = JsonDocument.Parse(bytes);
        Assert.Equal(choice, body.RootElement.GetProperty("tool_choice").GetString());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task CreateResponseAsync_ReturnsFunctionCallsEveryOutputItemAndUsage()
    {
        var reasoning = """{ "type": "reasoning", "id": "rs_1", "encrypted_content": "opaque" }""";
        var call = """{ "type": "function_call", "id": "fc_1", "call_id": "call_1", "name": "search_transactions", "arguments": "{\"query\":\"haircut\"}" }""";
        var handler = new QueueHandler(_ => Json(CompletedResponse(reasoning, call)));
        var client = NewClient(handler);

        var result = await client.CreateResponseAsync(NewRequest());

        var functionCall = Assert.Single(result.FunctionCalls);
        Assert.Equal("call_1", functionCall.CallId);
        Assert.Equal("search_transactions", functionCall.Name);
        Assert.Equal("{\"query\":\"haircut\"}", functionCall.ArgumentsJson);
        Assert.Equal(2, result.OutputItems.Count);
        Assert.Equal("opaque", result.OutputItems[0]["encrypted_content"]!.GetValue<string>());
        Assert.Equal("", result.Text);
        Assert.Equal(new AiTokenUsage(40, 30, 7, 3), result.Usage);
        Assert.Equal("test-model", result.Model);
    }

    [Fact]
    public async Task CreateResponseAsync_StreamsDeltasAndToolStartsAcrossArbitraryChunkBoundaries()
    {
        // "é" and "—" are multibyte in UTF-8; one-byte reads split them mid-character.
        var events = Sse(
            ("response.output_item.added", """{"type":"response.output_item.added","item":{"type":"function_call","name":"get_accounts"}}"""),
            ("response.output_text.delta", """{"type":"response.output_text.delta","delta":"Café "}"""),
            ("response.output_text.delta", """{"type":"response.output_text.delta","delta":"— done"}"""),
            ("response.completed", $$"""{"type":"response.completed","response":{{CompletedResponse(Message("Café — done"))}}}"""));
        var handler = new QueueHandler(_ => Stream(events, chunkSize: 1));
        var sink = new RecordingSink();

        var result = await NewClient(handler).CreateResponseAsync(NewRequest(), sink);

        Assert.Equal(["Café ", "— done"], sink.Deltas);
        Assert.Equal(["get_accounts"], sink.ToolStarts);
        Assert.Equal("Café — done", result.Text);
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task CreateResponseAsync_IgnoresHeartbeatCommentsAndDoneMarker()
    {
        var events = ": keep-alive\n\n" + Sse(
            ("response.completed", $$"""{"type":"response.completed","response":{{CompletedResponse(Message("ok"))}}}""")) +
            "data: [DONE]\n\n";
        var handler = new QueueHandler(_ => Stream(events, chunkSize: 7));

        var result = await NewClient(handler).CreateResponseAsync(NewRequest(), new RecordingSink());

        Assert.Equal("ok", result.Text);
    }

    [Fact]
    public async Task CreateResponseAsync_MapsStreamErrorEventToFriendlyError()
    {
        var events = Sse(
            ("response.output_text.delta", """{"type":"response.output_text.delta","delta":"partial"}"""),
            ("error", """{"type":"error","message":"server exploded"}"""));
        var handler = new QueueHandler(_ => Stream(events, chunkSize: 16));

        var ex = await Assert.ThrowsAsync<AiClientException>(() =>
            NewClient(handler).CreateResponseAsync(NewRequest(), new RecordingSink()));

        Assert.Equal("AI service returned an error. Please try again.", ex.Message);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task CreateResponseAsync_TreatsStreamWithoutTerminalEventAsError()
    {
        var events = Sse(("response.output_text.delta", """{"type":"response.output_text.delta","delta":"cut"}"""));
        var handler = new QueueHandler(_ => Stream(events, chunkSize: 16));

        await Assert.ThrowsAsync<AiClientException>(() =>
            NewClient(handler).CreateResponseAsync(NewRequest(), new RecordingSink()));
    }

    [Fact]
    public async Task CreateResponseAsync_MapsOutputLimitToExistingFriendlyError()
    {
        var incomplete = """{ "status": "incomplete", "incomplete_details": { "reason": "max_output_tokens" }, "output": [] }""";
        var handler = new QueueHandler(_ => Json(incomplete));

        var ex = await Assert.ThrowsAsync<AiClientException>(() => NewClient(handler).CreateResponseAsync(NewRequest()));

        Assert.Equal("AI response was too long and got cut off. Please try again.", ex.Message);
    }

    [Fact]
    public async Task CreateResponseAsync_RetriesOnceOnTransientStatusBeforeAnyStreamedByte()
    {
        var events = Sse(
            ("response.output_text.delta", """{"type":"response.output_text.delta","delta":"hello"}"""),
            ("response.completed", $$"""{"type":"response.completed","response":{{CompletedResponse(Message("hello"))}}}"""));
        var calls = 0;
        var handler = new QueueHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") }
            : Stream(events, chunkSize: 32));
        var sink = new RecordingSink();

        var result = await NewClient(handler).CreateResponseAsync(NewRequest(), sink);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(["hello"], sink.Deltas);
        Assert.Equal("hello", result.Text);
    }

    [Fact]
    public async Task CreateResponseAsync_ReportsDeadlineAsFriendlyErrorWithoutCallerCancellation()
    {
        var handler = new QueueHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return Json(CompletedResponse(Message("late")));
        });

        var ex = await Assert.ThrowsAsync<AiClientException>(() =>
            NewClient(handler).CreateResponseAsync(NewRequest() with { Timeout = TimeSpan.FromMilliseconds(50) }));

        Assert.Equal("AI took too long to respond. Please try again.", ex.Message);
    }

    [Fact]
    public async Task CreateResponseAsync_PropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new QueueHandler(async (_, token) =>
        {
            await cancellation.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return Json(CompletedResponse(Message("late")));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewClient(handler).CreateResponseAsync(NewRequest(), cancellationToken: cancellation.Token));
    }

    private static AiResponseRequest NewRequest() =>
        new("chat", [AiInputItems.Message("user", "when was my latest haircut?")], 500, "OpenAiModels:Chat");

    private static AiClient NewClient(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        TestHelpers.NewConfiguration(("OpenAiApiKey", "secret-key"), ("OpenAiModel", "test-model")),
        NullLogger<AiClient>.Instance);

    private static string Message(string text) =>
        $$"""{ "type": "message", "role": "assistant", "content": [{ "type": "output_text", "text": {{JsonSerializer.Serialize(text)}} }] }""";

    private static string CompletedResponse(params string[] items) =>
        $$"""
        {
          "status": "completed",
          "output": [{{string.Join(",", items)}}],
          "usage": {
            "input_tokens": 40, "output_tokens": 7,
            "input_tokens_details": { "cached_tokens": 30 },
            "output_tokens_details": { "reasoning_tokens": 3 }
          }
        }
        """;

    // SSE data must be single-line, so each payload is re-serialized compactly.
    private static string Sse(params (string Event, string Data)[] events) =>
        string.Concat(events.Select(e => $"event: {e.Event}\ndata: {JsonNode.Parse(e.Data)!.ToJsonString()}\n\n"));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Stream(string events, int chunkSize) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(new ChunkedStream(Encoding.UTF8.GetBytes(events), chunkSize))
    };

    private sealed class RecordingSink : IAiStreamSink
    {
        public List<string> Deltas { get; } = [];
        public List<string> ToolStarts { get; } = [];

        public ValueTask OnTextDeltaAsync(string delta, CancellationToken cancellationToken)
        {
            Deltas.Add(delta);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnToolCallStartedAsync(string toolName, CancellationToken cancellationToken)
        {
            ToolStarts.Add(toolName);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public QueueHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            _responder = (request, _) => Task.FromResult(responder(request));

        public QueueHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) =>
            _responder = responder;

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return await _responder(request, cancellationToken);
        }
    }

    // Delivers at most chunkSize bytes per read, the way a slow network hands over an SSE body.
    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var length = Math.Min(Math.Min(count, chunkSize), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, length);
            _position += length;
            return length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
