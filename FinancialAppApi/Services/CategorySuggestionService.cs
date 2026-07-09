using System.Net.Http.Headers;
using System.Text.Json;

namespace FinancialAppApi.Services;

public sealed record CategorySuggestion(string Category, double Confidence);

public sealed class CategorySuggestionService
{
    private readonly HttpClient _httpClient;
    private readonly TransactionCategoryService _categoryService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CategorySuggestionService> _logger;

    public CategorySuggestionService(
        HttpClient httpClient,
        TransactionCategoryService categoryService,
        IConfiguration configuration,
        ILogger<CategorySuggestionService> logger)
    {
        _httpClient = httpClient;
        _categoryService = categoryService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CategorySuggestion>> SuggestAsync(
        string description,
        string? txType,
        IEnumerable<string>? requestedCategories)
    {
        var categoryNames = NormalizeCategoryNames(requestedCategories).ToList();
        if (categoryNames.Count == 0)
        {
            categoryNames = (await _categoryService.GetCategoriesAsync())
                .Select(c => c.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name)
                .ToList();
        }

        if (categoryNames.Count == 0)
        {
            return [];
        }

        var apiKey = _configuration["GeminiApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new CategorySuggestionUserException("AI category suggestions are not configured.");
        }

        var safeTxType = string.Equals(txType, "inflow", StringComparison.OrdinalIgnoreCase)
            ? "inflow"
            : "outflow";
        var categoriesJson = JsonSerializer.Serialize(categoryNames);
        var prompt = $@"You suggest transaction categories for a personal finance app.
Score every available category internally for how well it matches the transaction description, then return only the top 3.
Return ONLY valid JSON, no markdown, no explanation.

Transaction description: {JsonSerializer.Serialize(description)}
Transaction type: {safeTxType}
Available categories JSON array: {categoriesJson}

JSON schema:
{{
  ""suggestions"": [
    {{ ""category"": ""<exact category name from the available list>"", ""confidence"": <0.0 to 1.0> }}
  ]
}}

Rules:
- category must be copied exactly from the available categories list.
- confidence is a number from 0.0 to 1.0.
- Return at most 3 suggestions, ordered from highest confidence to lowest.
- Do not include ledger categories. Do not create new category names.";

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = prompt }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.0,
                maxOutputTokens = 512
            }
        };

        var primaryModel = _configuration["GeminiModel"];
        if (string.IsNullOrWhiteSpace(primaryModel))
        {
            primaryModel = "gemini-3.1-flash-lite";
        }
        const string fallbackModel = "gemini-3.5-flash";

        string text;
        try
        {
            text = await CallGeminiAsync(primaryModel, requestBody, apiKey);
        }
        catch (GeminiUnavailableException)
        {
            _logger.LogWarning("Primary model {PrimaryModel} unavailable, retrying with fallback {FallbackModel}.", primaryModel, fallbackModel);
            text = await CallGeminiAsync(fallbackModel, requestBody, apiKey);
        }

        return ParseSuggestions(text, categoryNames);
    }

    public static IReadOnlyList<CategorySuggestion> ParseSuggestions(string text, IReadOnlyList<string> categoryNames)
    {
        text = StripMarkdownFence(text.Trim());

        using var resultDoc = JsonDocument.Parse(text);
        var root = resultDoc.RootElement;
        var suggestionsElement = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("suggestions", out var suggestionsProp)
                ? suggestionsProp
                : default;

        if (suggestionsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var categoryByName = categoryNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(name => name.Trim().ToLowerInvariant(), name => name.Trim());
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var suggestions = new List<CategorySuggestion>();

        foreach (var item in suggestionsElement.EnumerateArray())
        {
            if (!item.TryGetProperty("category", out var categoryProp))
            {
                continue;
            }

            var rawCategory = categoryProp.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(rawCategory) ||
                !categoryByName.TryGetValue(rawCategory.ToLowerInvariant(), out var canonicalCategory) ||
                !seen.Add(canonicalCategory))
            {
                continue;
            }

            var confidence = 0.5;
            if (item.TryGetProperty("confidence", out var confidenceProp) &&
                confidenceProp.ValueKind == JsonValueKind.Number)
            {
                confidence = confidenceProp.GetDouble();
                if (confidence > 1.0)
                {
                    confidence /= 100.0;
                }
            }

            confidence = Math.Clamp(confidence, 0.0, 1.0);
            suggestions.Add(new CategorySuggestion(canonicalCategory, confidence));
        }

        return suggestions
            .OrderByDescending(s => s.Confidence)
            .Take(3)
            .ToList();
    }

    private async Task<string> CallGeminiAsync(string model, object requestBody, string apiKey)
    {
        var geminiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, geminiUrl);
        httpRequest.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(requestBody));
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await _httpClient.SendAsync(httpRequest);

        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            var unavailableBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Gemini API 503 (model {Model}): {Body}", model, unavailableBody);
            throw new GeminiUnavailableException();
        }

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var quotaBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Gemini API 429 (model {Model}): {Body}", model, quotaBody);
            throw new CategorySuggestionUserException("AI service rate limit reached. Please wait a moment and try again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Gemini API error {Status} (model {Model}): {Body}", response.StatusCode, model, errorBody);
            throw new CategorySuggestionUserException("AI service returned an error. Please try again.");
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        using var geminiDoc = JsonDocument.Parse(responseBody);
        var candidate = geminiDoc.RootElement.GetProperty("candidates")[0];
        var text = candidate
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "";

        if (candidate.TryGetProperty("finishReason", out var finishReasonProp) &&
            finishReasonProp.GetString() == "MAX_TOKENS")
        {
            _logger.LogWarning("Gemini response truncated by MAX_TOKENS (model {Model}). Partial text: {RawText}", model, text);
            throw new CategorySuggestionUserException("AI response was too long and got cut off. Please try again.");
        }

        return text;
    }

    private static IEnumerable<string> NormalizeCategoryNames(IEnumerable<string>? requestedCategories)
    {
        return requestedCategories?
            .Select(c => c.Trim())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .Take(100) ?? [];
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

    private sealed class GeminiUnavailableException : Exception { }
}

public sealed class CategorySuggestionUserException : Exception
{
    public CategorySuggestionUserException(string message) : base(message)
    {
    }
}
