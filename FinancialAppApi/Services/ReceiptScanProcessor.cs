using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public record ReceiptScanResult(
    string Description,
    decimal? Amount,
    string? Date,
    string Category,
    string LedgerCategory,
    string TxType,
    double Confidence);

public class ReceiptScanProcessor
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReceiptScanProcessor> _logger;

    public ReceiptScanProcessor(AppDbContext context, IConfiguration configuration, ILogger<ReceiptScanProcessor> logger)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task ProcessAsync(string jobId)
    {
        var job = await _context.ReceiptScanJobs.FirstOrDefaultAsync(j => j.Id == jobId);
        if (job == null)
        {
            _logger.LogWarning("Receipt scan job {JobId} was not found.", jobId);
            return;
        }

        if (job.Status == "completed" || job.Status == "failed")
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(job.ImageBase64))
        {
            await MarkFailed(job, "Receipt image was not available for processing.");
            return;
        }

        job.Status = "processing";
        job.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        try
        {
            var result = await ScanImageAsync(job.ImageBase64, job.MimeType);
            job.Status = "completed";
            job.ResultJson = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            job.ErrorMessage = null;
            job.ImageBase64 = null;
            job.CompletedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }
        catch (ReceiptScanUserException ex)
        {
            _logger.LogWarning(ex, "Receipt scan job {JobId} failed with user-facing error.", jobId);
            await MarkFailed(job, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Receipt scan job {JobId} timed out.", jobId);
            await MarkFailed(job, "AI service timed out. Please try again.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Receipt scan job {JobId} returned invalid JSON.", jobId);
            await MarkFailed(job, "Could not read the receipt. Please try a clearer photo.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while processing receipt scan job {JobId}.", jobId);
            await MarkFailed(job, "An unexpected error occurred. Please try again.");
        }
    }

    private async Task MarkFailed(ReceiptScanJob job, string message)
    {
        job.Status = "failed";
        job.ErrorMessage = message;
        job.ImageBase64 = null;
        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    private async Task<ReceiptScanResult> ScanImageAsync(string base64Image, string mimeType)
    {
        var apiKey = _configuration["GeminiApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ReceiptScanUserException("OCR service is not configured. Ask your administrator to set the GeminiApiKey.");
        }

        var categories = await _context.TransactionCategories
            .OrderBy(c => c.Name)
            .Select(c => c.Name)
            .ToListAsync();

        var validLedgerCategories = new[] { "Essentials", "Growth", "Stability", "Rewards", "Income" };

        var prompt = $@"You are a receipt/invoice OCR assistant for a personal finance app.
Analyze this receipt image and extract the following fields. Return ONLY valid JSON, no markdown, no explanation.

JSON schema:
{{
  ""description"": ""<merchant name or brief description of purchase, e.g. 'McDonald's', 'Grab Ride', 'Electricity Bill'>"",
  ""amount"": <numeric value, positive number, e.g. 24.50 - extract the TOTAL amount paid>,
  ""date"": ""<ISO date string YYYY-MM-DD if visible on receipt, otherwise null>"",
  ""category"": ""<best-fit subcategory - MUST be one of the listed categories: {string.Join(", ", categories)} (fallback to 'Other')>"",
  ""ledgerCategory"": ""<best-fit ledger category - MUST be one of: {string.Join(", ", validLedgerCategories)}>"",
  ""txType"": ""outflow"",
  ""confidence"": <0.0 to 1.0 indicating how confident you are in the extracted data>
}}

Rules:
- description: use the merchant/store name if visible; otherwise describe the purchase type
- amount: extract the final TOTAL amount (after tax/tip if applicable); return as a plain number
- date: only return a date if you can clearly read it on the receipt; otherwise null
- category: pick the single most fitting category from this exact list: {string.Join(", ", categories)}. Do not make up your own category name.
- ledgerCategory: pick the single most fitting ledger category from this list: {string.Join(", ", validLedgerCategories)} (Essentials is typically for food/transport/bills, Rewards for entertainment/shopping, Growth for investments/education, Stability for savings/insurance, Income for salary/inflows).
- txType: always ""outflow"" for receipts (receipts are purchases)
- If you cannot read the receipt clearly, still return your best guess with a low confidence score

Return only the JSON object.";

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new
                        {
                            inline_data = new
                            {
                                mime_type = mimeType,
                                data = base64Image
                            }
                        },
                        new { text = prompt }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.1,
                maxOutputTokens = 1024
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
            // Primary model unavailable — silently retry with the fallback
            _logger.LogWarning("Primary model {PrimaryModel} unavailable, retrying with fallback {FallbackModel}.", primaryModel, fallbackModel);
            text = await CallGeminiAsync(fallbackModel, requestBody, apiKey);
        }

        text = text.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > 0 && lastFence > firstNewline)
            {
                text = text.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
            }
        }

        using var resultDoc = JsonDocument.Parse(text);
        var root = resultDoc.RootElement;

        string description = root.TryGetProperty("description", out var descProp) ? (descProp.GetString() ?? "") : "";
        decimal? amount = null;
        if (root.TryGetProperty("amount", out var amtProp) && amtProp.ValueKind == JsonValueKind.Number)
        {
            amount = amtProp.GetDecimal();
        }

        string? date = root.TryGetProperty("date", out var dateProp) && dateProp.ValueKind != JsonValueKind.Null
            ? dateProp.GetString()
            : null;
        string category = root.TryGetProperty("category", out var catProp) ? (catProp.GetString() ?? "") : "";
        string ledgerCategory = root.TryGetProperty("ledgerCategory", out var lcProp) ? (lcProp.GetString() ?? "Essentials") : "Essentials";
        string txType = root.TryGetProperty("txType", out var txTypeProp) ? (txTypeProp.GetString() ?? "outflow") : "outflow";
        double confidence = root.TryGetProperty("confidence", out var confProp) && confProp.ValueKind == JsonValueKind.Number
            ? confProp.GetDouble()
            : 0.5;

        if (!validLedgerCategories.Contains(ledgerCategory))
        {
            ledgerCategory = "Essentials";
        }

        return new ReceiptScanResult(description, amount, date, category, ledgerCategory, txType, confidence);
    }

    private async Task<string> CallGeminiAsync(string model, object requestBody, string apiKey)
    {
        var geminiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, geminiUrl);
        httpRequest.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(requestBody));
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await HttpClient.SendAsync(httpRequest);

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
            throw new ReceiptScanUserException("AI service rate limit reached. Please wait a moment and try again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Gemini API error {Status} (model {Model}): {Body}", response.StatusCode, model, errorBody);
            throw new ReceiptScanUserException("AI service returned an error. Please try again.");
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
            throw new ReceiptScanUserException("AI response was too long and got cut off. Please try again.");
        }

        return text;
    }

    private sealed class GeminiUnavailableException : Exception { }

    private sealed class ReceiptScanUserException : Exception
    {
        public ReceiptScanUserException(string message) : base(message)
        {
        }
    }
}
