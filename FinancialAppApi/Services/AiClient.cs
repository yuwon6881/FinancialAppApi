using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text.Json;
using FinancialAppApi.Diagnostics;

namespace FinancialAppApi.Services;

public sealed record AiInlineData(string MimeType, string Base64Data);

public sealed record AiPart
{
    public string? Text { get; init; }
    public AiInlineData? InlineData { get; init; }

    public static AiPart FromText(string text) => new() { Text = text };
    public static AiPart FromImage(string mimeType, string base64Data) => new() { InlineData = new AiInlineData(mimeType, base64Data) };
}

public sealed record AiGenerationOptions(
    string Feature,
    double Temperature,
    int MaxOutputTokens,
    string? SystemInstruction = null,
    object? ResponseJsonSchema = null,
    string? ThinkingLevel = "low",
    string ModelConfigurationKey = "AiModel");

// Single point of contact with the AI provider. Feature services supply a schema and a small,
// explicit compute budget; transport, retries, usage telemetry and provider parsing stay here.
public class AiClient
{
    private const string DefaultPrimaryModel = "gemini-3.5-flash-lite";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AiClient> _logger;

    public AiClient(HttpClient httpClient, IConfiguration configuration, ILogger<AiClient> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_configuration["AiApiKey"]);

    public async Task<string> GenerateTextAsync(
        IReadOnlyList<AiPart> parts,
        AiGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("AiClient.GenerateText");
        activity?.SetTag("ai.feature", options.Feature);
        activity?.SetTag("ai.model", options.ModelConfigurationKey);

        Telemetry.AiActionsCounter.Add(1, new KeyValuePair<string, object?>("feature", options.Feature));

        var apiKey = _configuration["AiApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AiClientException("AI service is not configured on the server.");
        }

        var requestBody = BuildRequestBody(parts, options);
        var primaryModel = _configuration[options.ModelConfigurationKey];
        if (string.IsNullOrWhiteSpace(primaryModel)) primaryModel = _configuration["AiModel"];
        if (string.IsNullOrWhiteSpace(primaryModel)) primaryModel = DefaultPrimaryModel;

        try
        {
            return await CallWithRetryAsync(primaryModel, requestBody, apiKey, options.Feature, cancellationToken);
        }
        catch (AiProviderUnavailableException) when (!cancellationToken.IsCancellationRequested)
        {
            // No fallback model: gemini-3.5-flash-lite is the only model. A transient provider
            // failure (already retried once by CallWithRetryAsync) surfaces as a clean error.
            throw new AiClientException("AI service is temporarily unavailable. Please try again.");
        }
    }

    internal static object BuildRequestBody(IReadOnlyList<AiPart> parts, AiGenerationOptions options)
    {
        var generationConfig = new Dictionary<string, object?>
        {
            ["temperature"] = options.Temperature,
            ["maxOutputTokens"] = options.MaxOutputTokens
        };
        if (!string.IsNullOrWhiteSpace(options.ThinkingLevel))
        {
            generationConfig["thinkingConfig"] = new { thinkingLevel = options.ThinkingLevel };
        }
        if (options.ResponseJsonSchema != null)
        {
            generationConfig["responseMimeType"] = "application/json";
            generationConfig["responseJsonSchema"] = options.ResponseJsonSchema;
        }

        var body = new Dictionary<string, object?>
        {
            ["contents"] = new[] { new { parts = parts.Select(ToWirePart).ToArray() } },
            ["generationConfig"] = generationConfig
        };
        if (!string.IsNullOrWhiteSpace(options.SystemInstruction))
        {
            body["systemInstruction"] = new { parts = new[] { new { text = options.SystemInstruction } } };
        }
        return body;
    }

    private static object ToWirePart(AiPart part)
    {
        if (part.InlineData != null)
        {
            return new { inline_data = new { mime_type = part.InlineData.MimeType, data = part.InlineData.Base64Data } };
        }
        return new { text = part.Text ?? "" };
    }

    private async Task<string> CallWithRetryAsync(
        string model,
        object requestBody,
        string apiKey,
        string feature,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CallAsync(model, requestBody, apiKey, feature, cancellationToken);
        }
        catch (AiProviderUnavailableException) when (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(RetryDelay, cancellationToken);
            return await CallAsync(model, requestBody, apiKey, feature, cancellationToken);
        }
    }

    private async Task<string> CallAsync(
        string model,
        object requestBody,
        string apiKey,
        string feature,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var requestUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUrl);
        httpRequest.Headers.Add("x-goog-api-key", apiKey);
        httpRequest.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(requestBody));
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "AI provider request failed for {Feature} using {Model}.", feature, model);
            throw new AiClientException("AI service is unreachable. Please try again.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Read the provider's error body: for a 400 it carries the actual reason
                // (e.g. an INVALID_ARGUMENT naming the rejected field or an over-complex
                // response schema). Without it a 400 is undiagnosable from logs alone.
                var errorBody = await SafeReadErrorBodyAsync(response, cancellationToken);

                if (IsTransientProviderFailure(response.StatusCode))
                {
                    _logger.LogWarning(
                        "AI provider returned transient status {Status} for {Feature} using {Model}. Body: {Body}",
                        (int)response.StatusCode,
                        feature,
                        model,
                        errorBody);
                    throw new AiProviderUnavailableException();
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning(
                        "AI provider rate limit reached for {Feature} using {Model}. Body: {Body}",
                        feature,
                        model,
                        errorBody);
                    throw new AiClientException("AI service rate limit reached. Please wait a moment and try again.");
                }

                _logger.LogWarning(
                    "AI provider returned status {Status} for {Feature} using {Model}. Body: {Body}",
                    (int)response.StatusCode,
                    feature,
                    model,
                    errorBody);
                throw new AiClientException("AI service returned an error. Please try again.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var responseDoc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var text = ParseResponse(responseDoc.RootElement, model, feature);
            _logger.LogInformation(
                "AI request {Feature}/{Model} completed in {ElapsedMs:F0} ms.",
                feature,
                model,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            return text;
        }
    }

    private string ParseResponse(JsonElement root, string model, string feature)
    {
        LogUsage(root, model, feature);

        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            var blockReason = root.TryGetProperty("promptFeedback", out var feedback) &&
                feedback.TryGetProperty("blockReason", out var blockReasonProperty)
                    ? blockReasonProperty.GetString()
                    : null;
            _logger.LogWarning(
                "AI provider returned no candidates for {Feature} using {Model}. Block reason: {BlockReason}.",
                feature,
                model,
                blockReason);
            throw new AiClientException("AI could not process this request. Please rephrase and try again.");
        }

        var candidate = candidates[0];
        var finishReason = candidate.TryGetProperty("finishReason", out var finishReasonProperty)
            ? finishReasonProperty.GetString()
            : null;
        if (!candidate.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var candidateParts) ||
            candidateParts.ValueKind != JsonValueKind.Array ||
            candidateParts.GetArrayLength() == 0)
        {
            _logger.LogWarning(
                "AI provider returned empty content for {Feature} using {Model}. Finish reason: {FinishReason}.",
                feature,
                model,
                finishReason);
            throw new AiClientException(finishReason is "SAFETY" or "PROHIBITED_CONTENT"
                ? "AI declined to respond to this request. Please rephrase and try again."
                : "AI could not process this request. Please try again.");
        }

        var text = string.Concat(candidateParts.EnumerateArray()
            .Where(part => part.TryGetProperty("text", out _))
            .Select(part => part.GetProperty("text").GetString()));
        if (finishReason == "MAX_TOKENS")
        {
            _logger.LogWarning("AI response reached its output limit for {Feature} using {Model}.", feature, model);
            throw new AiClientException("AI response was too long and got cut off. Please try again.");
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new AiClientException("AI returned an empty response. Please try again.");
        }

        return StripMarkdownFence(text.Trim());
    }

    private void LogUsage(JsonElement root, string model, string feature)
    {
        if (!root.TryGetProperty("usageMetadata", out var usage)) return;
        _logger.LogInformation(
            "AI usage {Feature}/{Model}: prompt={PromptTokens}, output={OutputTokens}, thoughts={ThoughtTokens}, cached={CachedTokens}.",
            feature,
            model,
            ReadTokenCount(usage, "promptTokenCount"),
            ReadTokenCount(usage, "candidatesTokenCount"),
            ReadTokenCount(usage, "thoughtsTokenCount"),
            ReadTokenCount(usage, "cachedContentTokenCount"));
    }

    private static int ReadTokenCount(JsonElement usage, string propertyName) =>
        usage.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var count) ? count : 0;

    private static bool IsTransientProviderFailure(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static async Task<string> SafeReadErrorBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            const int maxLength = 2000;
            return body.Length <= maxLength ? body : string.Concat(body.AsSpan(0, maxLength), "...(truncated)");
        }
        catch (Exception ex)
        {
            return $"<failed to read error body: {ex.Message}>";
        }
    }

    private static string StripMarkdownFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstNewline = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline > 0 && lastFence > firstNewline
            ? text.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim()
            : text;
    }

    private sealed class AiProviderUnavailableException : Exception;
}

public sealed class AiClientException : Exception
{
    public AiClientException(string message) : base(message) { }
}
