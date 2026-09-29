using System.Text;
using System.Text.Json;

namespace FinancialAppApi.Services;

// Parses the Responses API server-sent event stream. Deltas go to the sink as they arrive; the
// terminal event carries the complete response object, which the caller parses exactly like a
// non-streamed body so both paths share one interpretation of output, usage, and status.
internal static class AiResponseStreamReader
{
    public static async Task<JsonElement> ReadAsync(Stream stream, IAiStreamSink sink, CancellationToken cancellationToken)
    {
        // StreamReader decodes across chunk boundaries, so a multibyte character split between
        // two network reads is never corrupted.
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        var data = new StringBuilder();
        JsonElement? final = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                final = await DispatchAsync(eventName, data, sink, cancellationToken) ?? final;
                eventName = null;
                data.Clear();
                continue;
            }
            if (line[0] == ':') continue;
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..]);
            }
        }
        final = await DispatchAsync(eventName, data, sink, cancellationToken) ?? final;

        return final ?? throw new AiStreamProtocolException("stream ended without a terminal response event");
    }

    private static async Task<JsonElement?> DispatchAsync(
        string? eventName,
        StringBuilder data,
        IAiStreamSink sink,
        CancellationToken cancellationToken)
    {
        if (data.Length == 0) return null;
        var payload = data.ToString();
        if (payload == "[DONE]") return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new AiStreamProtocolException($"malformed event data: {ex.Message}");
        }
        using var _ = document;
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : eventName;
        switch (type)
        {
            case "response.output_text.delta":
                if (root.TryGetProperty("delta", out var delta) && delta.GetString() is { Length: > 0 } text)
                    await sink.OnTextDeltaAsync(text, cancellationToken);
                return null;
            case "response.output_item.added":
                if (root.TryGetProperty("item", out var item) &&
                    item.TryGetProperty("type", out var itemType) && itemType.GetString() == "function_call" &&
                    item.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } toolName)
                {
                    await sink.OnToolCallStartedAsync(toolName, cancellationToken);
                }
                return null;
            case "response.completed":
            case "response.incomplete":
            case "response.failed":
                return root.TryGetProperty("response", out var response)
                    ? response.Clone()
                    : throw new AiStreamProtocolException($"{type} event carried no response");
            case "error":
                var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
                throw new AiStreamProtocolException(message ?? "provider stream error");
            default:
                return null;
        }
    }
}

internal sealed class AiStreamProtocolException(string detail) : Exception(detail);
