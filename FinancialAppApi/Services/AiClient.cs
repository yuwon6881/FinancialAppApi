using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FinancialAppApi.Services;

public sealed record AiInlineData(string MimeType, string Base64Data);

public sealed record AiPart
{
    public string? Text { get; init; }
    public AiInlineData? InlineData { get; init; }

    public static AiPart FromText(string text) => new() { Text = text };
    public static AiPart FromImage(string mimeType, string base64Data) => new() { InlineData = new AiInlineData(mimeType, base64Data) };
}

// Single point of contact with the AI provider (currently Gemini, called over its REST API).
// Every AI feature (Ask AI, category suggestions/cleanup, receipt OCR) used to hand-roll its
// own HTTP call, model-fallback retry, and response parsing -- this centralizes that so a fix
// (e.g. handling a safety-blocked response) applies everywhere at once instead of needing
// three edits, and so swapping providers later only means changing this one file.
public class AiClient
{
    private const string FallbackModel = "gemini-3.5-flash";
    private const string DefaultPrimaryModel = "gemini-3.1-flash-lite";

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

    // systemInstruction should hold the stable, request-independent portion of a prompt
    // (rules, output schema, allowed actions) so it forms an identical prefix across calls --
    // that is what makes it eligible for the provider's automatic prefix caching. Put only the
    // per-request data (user message, live app context, image bytes) in `parts`.
    public async Task<string> GenerateTextAsync(
        IReadOnlyList<AiPart> parts,
        double temperature,
        int maxOutputTokens,
        string? systemInstruction = null)
    {
        var apiKey = _configuration["AiApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AiClientException("AI service is not configured on the server.");
        }

        var requestBody = BuildRequestBody(parts, temperature, maxOutputTokens, systemInstruction);

        var primaryModel = _configuration["AiModel"];
        if (string.IsNullOrWhiteSpace(primaryModel))
        {
            primaryModel = DefaultPrimaryModel;
        }

        try
        {
            return await CallAsync(primaryModel, requestBody, apiKey);
        }
        catch (AiProviderUnavailableException)
        {
            _logger.LogWarning("Primary model {PrimaryModel} unavailable, retrying with fallback {FallbackModel}.", primaryModel, FallbackModel);
            return await CallAsync(FallbackModel, requestBody, apiKey);
        }
    }

    private static object BuildRequestBody(IReadOnlyList<AiPart> parts, double temperature, int maxOutputTokens, string? systemInstruction)
    {
        var contentParts = parts.Select(ToWirePart).ToArray();
        var generationConfig = new { temperature, maxOutputTokens };

        if (string.IsNullOrEmpty(systemInstruction))
        {
            return new
            {
                contents = new[] { new { parts = contentParts } },
                generationConfig
            };
        }

        return new
        {
            systemInstruction = new { parts = new[] { new { text = systemInstruction } } },
            contents = new[] { new { parts = contentParts } },
            generationConfig
        };
    }

    private static object ToWirePart(AiPart part)
    {
        if (part.InlineData != null)
        {
            return new { inline_data = new { mime_type = part.InlineData.MimeType, data = part.InlineData.Base64Data } };
        }
        return new { text = part.Text ?? "" };
    }

    private async Task<string> CallAsync(string model, object requestBody, string apiKey)
    {
        var requestUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUrl);
        httpRequest.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(requestBody));
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "AI provider request failed (model {Model}).", model);
            throw new AiClientException("AI service is unreachable. Please try again.");
        }

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            var unavailableBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("AI provider returned 503 (model {Model}): {Body}", model, unavailableBody);
            throw new AiProviderUnavailableException();
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var quotaBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("AI provider returned 429 (model {Model}): {Body}", model, quotaBody);
            throw new AiClientException("AI service rate limit reached. Please wait a moment and try again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("AI provider returned error {Status} (model {Model}): {Body}", response.StatusCode, model, errorBody);
            throw new AiClientException("AI service returned an error. Please try again.");
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        using var responseDoc = JsonDocument.Parse(responseBody);
        var root = responseDoc.RootElement;

        if (!root.TryGetProperty("candidates", out var candidatesProp) ||
            candidatesProp.ValueKind != JsonValueKind.Array ||
            candidatesProp.GetArrayLength() == 0)
        {
            // The provider can return 200 OK with no candidates when the prompt itself is
            // blocked (promptFeedback.blockReason) -- previously this fell through to an
            // unhandled exception (GetProperty("candidates")[0]) instead of a friendly message.
            var blockReason = root.TryGetProperty("promptFeedback", out var feedbackProp) &&
                feedbackProp.TryGetProperty("blockReason", out var blockReasonProp)
                    ? blockReasonProp.GetString()
                    : null;
            _logger.LogWarning("AI provider returned no candidates (model {Model}). BlockReason: {BlockReason}. Body: {Body}", model, blockReason, responseBody);
            throw new AiClientException("AI could not process this request. Please rephrase and try again.");
        }

        var candidate = candidatesProp[0];
        var finishReason = candidate.TryGetProperty("finishReason", out var finishReasonProp) ? finishReasonProp.GetString() : null;

        if (!candidate.TryGetProperty("content", out var contentProp) ||
            !contentProp.TryGetProperty("parts", out var candidatePartsProp) ||
            candidatePartsProp.ValueKind != JsonValueKind.Array ||
            candidatePartsProp.GetArrayLength() == 0)
        {
            // A candidate can be returned with an empty content body when the *response*
            // (not the prompt) trips a safety filter -- same missing-data shape as above.
            _logger.LogWarning("AI provider returned a candidate with no content (model {Model}). FinishReason: {FinishReason}. Body: {Body}", model, finishReason, responseBody);
            throw new AiClientException(finishReason is "SAFETY" or "PROHIBITED_CONTENT"
                ? "AI declined to respond to this request. Please rephrase and try again."
                : "AI could not process this request. Please try again.");
        }

        var text = candidatePartsProp[0].TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";

        if (finishReason == "MAX_TOKENS")
        {
            _logger.LogWarning("AI provider response truncated by MAX_TOKENS (model {Model}). Partial text: {RawText}", model, text);
            throw new AiClientException("AI response was too long and got cut off. Please try again.");
        }

        return StripMarkdownFence(text.Trim());
    }

    private static string StripMarkdownFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewline = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        if (firstNewline > 0 && lastFence > firstNewline)
        {
            return text.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
        }

        return text;
    }

    // Internal-only signal used to trigger the same-request fallback-model retry; never
    // surfaced to callers.
    private sealed class AiProviderUnavailableException : Exception { }
}

public sealed class AiClientException : Exception
{
    public AiClientException(string message) : base(message)
    {
    }
}
