using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text.Json;
using FinancialAppApi.Diagnostics;

namespace FinancialAppApi.Services;

public sealed record AiInlineData(string MimeType, string Base64Data);
public sealed record AiFileData(string FileName, string MimeType, string Base64Data);

public sealed record AiPart
{
    public string? Text { get; init; }
    public AiInlineData? InlineData { get; init; }
    public AiFileData? FileData { get; init; }

    public static AiPart FromText(string text) => new() { Text = text };
    public static AiPart FromImage(string mimeType, string base64Data) => new() { InlineData = new AiInlineData(mimeType, base64Data) };
    public static AiPart FromFile(string fileName, string mimeType, string base64Data) =>
        new() { FileData = new AiFileData(fileName, mimeType, base64Data) };
}

public sealed record AiGenerationOptions(
    string Feature,
    double Temperature,
    int MaxOutputTokens,
    string? SystemInstruction = null,
    object? OutputJsonSchema = null,
    string? ThinkingLevel = "low",
    string ModelConfigurationKey = "OpenAiModel");

// Single point of contact with the AI provider. Feature services supply a schema and a small,
// explicit compute budget; transport, retries, usage telemetry and provider parsing stay here.
public class AiClient
{
    private const string DefaultPrimaryModel = "gpt-5.4-mini";
    private const string ResponsesEndpoint = "https://api.openai.com/v1/responses";
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

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_configuration["OpenAiApiKey"]);

    public async Task<string> GenerateTextAsync(
        IReadOnlyList<AiPart> parts,
        AiGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("AiClient.GenerateText");
        activity?.SetTag("ai.feature", options.Feature);
        activity?.SetTag("ai.model", options.ModelConfigurationKey);

        Telemetry.AiActionsCounter.Add(1, new KeyValuePair<string, object?>("feature", options.Feature));

        var apiKey = _configuration["OpenAiApiKey"]?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AiClientException("AI service is not configured on the server.");
        }

        var primaryModel = _configuration[options.ModelConfigurationKey];
        if (string.IsNullOrWhiteSpace(primaryModel)) primaryModel = _configuration["OpenAiModel"];
        if (string.IsNullOrWhiteSpace(primaryModel)) primaryModel = DefaultPrimaryModel;
        var requestBody = BuildRequestBody(parts, options, primaryModel);

        try
        {
            return await CallWithRetryAsync(primaryModel, requestBody, apiKey, options.Feature, cancellationToken);
        }
        catch (AiProviderUnavailableException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiClientException("AI service is temporarily unavailable. Please try again.");
        }
    }

    internal static object BuildRequestBody(
        IReadOnlyList<AiPart> parts,
        AiGenerationOptions options,
        string model)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["store"] = false,
            ["input"] = new[]
            {
                new
                {
                    role = "user",
                    content = parts.Select(ToWirePart).ToArray()
                }
            },
            ["max_output_tokens"] = options.MaxOutputTokens
        };

        if (!string.IsNullOrWhiteSpace(options.ThinkingLevel))
        {
            body["reasoning"] = new { effort = options.ThinkingLevel };
        }
        if (options.OutputJsonSchema != null)
        {
            body["text"] = new
            {
                format = new
                {
                    type = "json_schema",
                    name = SchemaNameFor(options.Feature),
                    strict = false,
                    schema = options.OutputJsonSchema
                }
            };
        }
        if (!string.IsNullOrWhiteSpace(options.SystemInstruction))
        {
            body["instructions"] = options.SystemInstruction;
        }
        return body;
    }

    private static object ToWirePart(AiPart part)
    {
        if (part.FileData != null)
        {
            return new
            {
                type = "input_file",
                filename = part.FileData.FileName,
                file_data = $"data:{part.FileData.MimeType};base64,{part.FileData.Base64Data}",
                detail = "low"
            };
        }
        if (part.InlineData != null)
        {
            return new
            {
                type = "input_image",
                image_url = $"data:{part.InlineData.MimeType};base64,{part.InlineData.Base64Data}",
                detail = "auto"
            };
        }
        return new { type = "input_text", text = part.Text ?? "" };
    }

    private static string SchemaNameFor(string feature)
    {
        var name = new string(feature
            .Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(name) ? "financial_app_output" : name[..Math.Min(name.Length, 64)];
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
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ResponsesEndpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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

        var status = root.TryGetProperty("status", out var statusProperty)
            ? statusProperty.GetString()
            : null;
        var incompleteReason = root.TryGetProperty("incomplete_details", out var incompleteDetails) &&
            incompleteDetails.ValueKind == JsonValueKind.Object &&
            incompleteDetails.TryGetProperty("reason", out var reasonProperty)
                ? reasonProperty.GetString()
                : null;
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

        string? refusal = null;
        var textParts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in content.EnumerateArray())
            {
                var type = part.TryGetProperty("type", out var typeProperty) ? typeProperty.GetString() : null;
                if (type == "output_text" && part.TryGetProperty("text", out var textProperty))
                    textParts.Add(textProperty.GetString() ?? "");
                else if (type == "refusal" && part.TryGetProperty("refusal", out var refusalProperty))
                    refusal = refusalProperty.GetString();
            }
        }

        var text = string.Concat(textParts);
        if (string.IsNullOrWhiteSpace(text))
        {
            if (!string.IsNullOrWhiteSpace(refusal))
            {
                _logger.LogWarning("AI declined {Feature} using {Model}.", feature, model);
                throw new AiClientException("AI declined to respond to this request. Please rephrase and try again.");
            }
            throw new AiClientException("AI returned an empty response. Please try again.");
        }

        return StripMarkdownFence(text.Trim());
    }

    private void LogUsage(JsonElement root, string model, string feature)
    {
        if (!root.TryGetProperty("usage", out var usage)) return;
        var reasoningTokens = usage.TryGetProperty("output_tokens_details", out var outputDetails)
            ? ReadTokenCount(outputDetails, "reasoning_tokens")
            : 0;
        var cachedTokens = usage.TryGetProperty("input_tokens_details", out var inputDetails)
            ? ReadTokenCount(inputDetails, "cached_tokens")
            : 0;
        _logger.LogInformation(
            "AI usage {Feature}/{Model}: input={InputTokens}, output={OutputTokens}, reasoning={ReasoningTokens}, cached={CachedTokens}.",
            feature,
            model,
            ReadTokenCount(usage, "input_tokens"),
            ReadTokenCount(usage, "output_tokens"),
            reasoningTokens,
            cachedTokens);
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
