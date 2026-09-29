using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FinancialAppApi.Diagnostics;

namespace FinancialAppApi.Services;

// Multi-round, tool-calling access to the Responses API. GenerateTextAsync remains the
// single-shot path for features that need one structured answer; this path returns function
// calls and every output item so a caller can run tools and continue the same reasoning.
public partial class AiClient
{
    // The named HttpClient timeout bounds only the wait for response headers, which arrive
    // immediately on a stream. This deadline bounds the whole round, body included.
    private static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(45);

    public async Task<AiResponseResult> CreateResponseAsync(
        AiResponseRequest request,
        IAiStreamSink? sink = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("AiClient.CreateResponse");
        activity?.SetTag("ai.feature", request.Feature);
        activity?.SetTag("ai.stream", sink != null);
        activity?.SetTag("ai.tools", request.Tools?.Count ?? 0);
        Telemetry.AiActionsCounter.Add(1, new KeyValuePair<string, object?>("feature", request.Feature));

        var apiKey = RequireApiKey();
        var model = ResolveModel(request.ModelConfigurationKey);
        activity?.SetTag("ai.model", model);
        activity?.SetTag("ai.model_key", request.ModelConfigurationKey);
        var body = BuildResponsesRequestBody(request, model, stream: sink != null);

        using var timeout = new CancellationTokenSource(request.Timeout ?? DefaultResponseTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            try
            {
                return await SendResponsesRoundAsync(model, body, apiKey, request, sink, linked.Token);
            }
            // A transient status arrives before any body byte, so nothing has reached the sink
            // yet and one retry cannot duplicate streamed text.
            catch (AiProviderUnavailableException) when (!linked.IsCancellationRequested)
            {
                await Task.Delay(RetryDelay, linked.Token);
                return await SendResponsesRoundAsync(model, body, apiKey, request, sink, linked.Token);
            }
        }
        catch (AiProviderUnavailableException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiClientException("AI service is temporarily unavailable. Please try again.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("AI request {Feature}/{Model} exceeded its deadline.", request.Feature, model);
            throw new AiClientException("AI took too long to respond. Please try again.");
        }
    }

    private async Task<AiResponseResult> SendResponsesRoundAsync(
        string model,
        byte[] body,
        string apiKey,
        AiResponseRequest request,
        IAiStreamSink? sink,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        using var response = await SendToProviderAsync(model, body, apiKey, request.Feature, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        AiResponseResult result;
        if (sink == null)
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            result = ParseResponsesResult(document.RootElement, model, request);
        }
        else
        {
            JsonElement final;
            try
            {
                final = await AiResponseStreamReader.ReadAsync(stream, sink, cancellationToken);
            }
            catch (AiStreamProtocolException ex)
            {
                _logger.LogWarning("AI stream failed for {Feature} using {Model}: {Detail}", request.Feature, model, ex.Message);
                throw new AiClientException("AI service returned an error. Please try again.");
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "AI stream was interrupted for {Feature} using {Model}.", request.Feature, model);
                throw new AiClientException("The AI connection was interrupted. Please try again.");
            }
            result = ParseResponsesResult(final, model, request);
        }

        _logger.LogInformation(
            "AI request {Feature}/{Model} completed in {ElapsedMs:F0} ms with {ToolCalls} tool call(s).",
            request.Feature,
            model,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            result.FunctionCalls.Count);
        return result;
    }

    internal static byte[] BuildResponsesRequestBody(AiResponseRequest request, string model, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["store"] = false,
            ["input"] = new JsonArray(request.Input.Select(item => (JsonNode)item.DeepClone()).ToArray()),
            ["max_output_tokens"] = request.MaxOutputTokens
        };
        if (!string.IsNullOrWhiteSpace(request.Instructions)) body["instructions"] = request.Instructions;
        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
        {
            body["reasoning"] = new JsonObject { ["effort"] = request.ReasoningEffort };
            // With store=false the provider keeps no reasoning between rounds; the encrypted
            // copy is what the caller echoes back so a tool round continues the same thought.
            body["include"] = new JsonArray("reasoning.encrypted_content");
        }
        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray(tools.Select(tool => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = tool.Parameters.DeepClone(),
                ["strict"] = tool.Strict
            }).ToArray());
            body["parallel_tool_calls"] = request.ParallelToolCalls;
        }
        if (!string.IsNullOrWhiteSpace(request.ToolChoice))
        {
            body["tool_choice"] = request.ToolChoice is "auto" or "none" or "required"
                ? request.ToolChoice
                : new JsonObject { ["type"] = "function", ["name"] = request.ToolChoice };
        }
        if (request.OutputJsonSchema != null)
        {
            body["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = SchemaNameFor(request.Feature),
                    ["strict"] = false,
                    ["schema"] = JsonSerializer.SerializeToNode(request.OutputJsonSchema)
                }
            };
        }
        if (!string.IsNullOrWhiteSpace(request.PromptCacheKey)) body["prompt_cache_key"] = request.PromptCacheKey;
        if (stream) body["stream"] = true;
        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    private AiResponseResult ParseResponsesResult(JsonElement root, string model, AiResponseRequest request)
    {
        var feature = request.Feature;
        var usage = LogUsage(root, model, feature);
        var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
        var incompleteReason = root.TryGetProperty("incomplete_details", out var details) &&
            details.ValueKind == JsonValueKind.Object &&
            details.TryGetProperty("reason", out var reason)
                ? reason.GetString()
                : null;

        if (status == "failed")
        {
            _logger.LogWarning(
                "AI provider reported a failed response for {Feature} using {Model}: {Error}",
                feature,
                model,
                root.TryGetProperty("error", out var error) ? error.GetRawText() : null);
            throw new AiClientException("AI could not process this request. Please try again.");
        }
        if (status == "incomplete" && incompleteReason == "max_output_tokens")
        {
            _logger.LogWarning("AI response reached its output limit for {Feature} using {Model}.", feature, model);
            throw new AiClientException("AI response was too long and got cut off. Please try again.");
        }
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning(
                "AI provider returned no output for {Feature} using {Model}. Status: {Status}; reason: {Reason}.",
                feature,
                model,
                status,
                incompleteReason);
            throw new AiClientException("AI could not process this request. Please try again.");
        }

        var items = new List<JsonObject>();
        var calls = new List<AiFunctionCall>();
        var textParts = new List<string>();
        string? refusal = null;
        foreach (var item in output.EnumerateArray())
        {
            if (JsonNode.Parse(item.GetRawText()) is JsonObject copy) items.Add(copy);
            var type = item.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (type == "function_call")
            {
                calls.Add(new AiFunctionCall(
                    item.TryGetProperty("call_id", out var callId) ? callId.GetString() ?? "" : "",
                    item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    item.TryGetProperty("arguments", out var arguments) ? arguments.GetString() ?? "{}" : "{}"));
                continue;
            }
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in content.EnumerateArray())
            {
                var partType = part.TryGetProperty("type", out var partTypeElement) ? partTypeElement.GetString() : null;
                if (partType == "output_text" && part.TryGetProperty("text", out var text))
                    textParts.Add(text.GetString() ?? "");
                else if (partType == "refusal" && part.TryGetProperty("refusal", out var refusalElement))
                    refusal = refusalElement.GetString();
            }
        }

        var joined = string.Concat(textParts).Trim();
        if (calls.Count == 0 && joined.Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(refusal))
            {
                _logger.LogWarning("AI declined {Feature} using {Model}.", feature, model);
                throw new AiClientException("AI declined to respond to this request. Please rephrase and try again.");
            }
            throw new AiClientException("AI returned an empty response. Please try again.");
        }

        return new AiResponseResult(
            model,
            request.OutputJsonSchema != null ? StripMarkdownFence(joined) : joined,
            calls,
            items,
            usage);
    }
}
