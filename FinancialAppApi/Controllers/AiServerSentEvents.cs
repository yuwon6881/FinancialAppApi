using System.Text;
using System.Text.Json;
using FinancialAppApi.Services.AI.Agent;
using Microsoft.AspNetCore.Http.Features;

namespace FinancialAppApi.Controllers;

// Writes one Ask AI turn as server-sent events:
//   status {label}      a tool is running ("Searching your transactions for ...")
//   delta  {text}       provisional reply text
//   reset  {}           drop provisional text (the model went on to call tools)
//   done   {response}   the authoritative AiChatResponse; replaces any streamed text
//   error  {status, response}  the same 409/429/503 meaning the JSON endpoint returns
// A comment line every 15 seconds keeps proxies from closing a quiet connection.
public sealed class AiServerSentEvents : IAiAgentProgressSink, IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpResponse _response;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _heartbeatStop;
    private readonly Task _heartbeat;

    private AiServerSentEvents(HttpResponse response, CancellationToken requestAborted)
    {
        _response = response;
        _heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        _heartbeat = HeartbeatAsync(_heartbeatStop.Token);
    }

    public static async Task<AiServerSentEvents> StartAsync(HttpResponse response, CancellationToken requestAborted)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache, no-transform";
        // Tells nginx-style proxies not to hold the stream back until it ends.
        response.Headers["X-Accel-Buffering"] = "no";
        response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await response.StartAsync(requestAborted);
        return new AiServerSentEvents(response, requestAborted);
    }

    public ValueTask OnStatusAsync(string label, CancellationToken cancellationToken) =>
        WriteAsync("status", new { label }, cancellationToken);

    public ValueTask OnTextDeltaAsync(string delta, CancellationToken cancellationToken) =>
        WriteAsync("delta", new { text = delta }, cancellationToken);

    public ValueTask OnTextResetAsync(CancellationToken cancellationToken) =>
        WriteAsync("reset", new { }, cancellationToken);

    public ValueTask DoneAsync(object response, CancellationToken cancellationToken) =>
        WriteAsync("done", response, cancellationToken);

    public ValueTask ErrorAsync(int status, object response, CancellationToken cancellationToken) =>
        WriteAsync("error", new { status, response }, cancellationToken);

    private async ValueTask WriteAsync(string eventName, object data, CancellationToken cancellationToken)
    {
        var payload = $"event: {eventName}\ndata: {JsonSerializer.Serialize(data, JsonOptions)}\n\n";
        await WriteRawAsync(payload, cancellationToken);
    }

    private async Task WriteRawAsync(string text, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
            await _response.Body.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await WriteRawAsync(": keep-alive\n\n", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The turn finished or the client left.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _heartbeatStop.CancelAsync();
        await _heartbeat;
        _heartbeatStop.Dispose();
        _writeLock.Dispose();
    }
}
