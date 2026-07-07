using Microsoft.AspNetCore.Mvc;
using FinancialAppApi.Filters;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/ocr")]
[AuthorizeToken]
public class OcrController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<OcrController> _logger;

    // Reuse one HttpClient for the lifetime of the controller (singleton via DI would
    // be cleaner but this avoids touching Program.cs / DI setup for a single client).
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public OcrController(IConfiguration configuration, ILogger<OcrController> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("scan-receipt")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB
    public async Task<IActionResult> ScanReceipt(IFormFile? image)
    {
        if (image == null || image.Length == 0)
            return BadRequest(new { message = "No image file provided." });

        var apiKey = _configuration["GeminiApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return StatusCode(503, new { message = "OCR service is not configured. Ask your administrator to set the GeminiApiKey." });

        // Convert upload to base64
        string base64Image;
        string mimeType;
        using (var ms = new MemoryStream())
        {
            await image.CopyToAsync(ms);
            base64Image = Convert.ToBase64String(ms.ToArray());
        }
        mimeType = image.ContentType switch
        {
            "image/png" => "image/png",
            "image/webp" => "image/webp",
            "image/gif" => "image/gif",
            _ => "image/jpeg"
        };

        // Build Gemini request payload
        var prompt = @"You are a receipt/invoice OCR assistant for a personal finance app.
Analyze this receipt image and extract the following fields. Return ONLY valid JSON, no markdown, no explanation.

JSON schema:
{
  ""description"": ""<merchant name or brief description of purchase, e.g. 'McDonald's', 'Grab Ride', 'Electricity Bill'>"",
  ""amount"": <numeric value, positive number, e.g. 24.50 — extract the TOTAL amount paid>,
  ""date"": ""<ISO date string YYYY-MM-DD if visible on receipt, otherwise null>"",
  ""category"": ""<best-fit subcategory, e.g. 'Food & Dining', 'Transport', 'Utilities', 'Groceries', 'Shopping', 'Entertainment', 'Healthcare' — pick the most appropriate>"",
  ""ledgerCategory"": ""<one of: Essentials, Growth, Stability, Rewards — pick based on spending type: Essentials for bills/food/transport/utilities, Rewards for dining-out/entertainment/shopping, Growth for investments/education, Stability for savings/insurance>"",
  ""txType"": ""outflow"",
  ""confidence"": <0.0 to 1.0 indicating how confident you are in the extracted data>
}

Rules:
- description: use the merchant/store name if visible; otherwise describe the purchase type
- amount: extract the final TOTAL amount (after tax/tip if applicable); return as a plain number
- date: only return a date if you can clearly read it on the receipt; otherwise null
- category: pick the single most fitting category
- ledgerCategory: most receipts are Essentials (food, transport, utilities) or Rewards (restaurants, entertainment)
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

        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(requestBody);
        // Model is configurable via "GeminiModel". Default to gemini-3.5-flash: it has a
        // generous free tier and is more than capable for receipt OCR. Avoid the flagship
        // gemini-3.x flash models here — they carry little/no free-tier quota, so a free
        // API key gets a 429 RESOURCE_EXHAUSTED on the very first request.
        var model = _configuration["GeminiModel"];
        if (string.IsNullOrWhiteSpace(model))
            model = "gemini-3.5-flash";
        var geminiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        string? rawText = null;
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, geminiUrl);
            httpRequest.Content = new ByteArrayContent(requestBytes);
            httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var response = await _httpClient.SendAsync(httpRequest);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var quotaBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Gemini API 429 (model {Model}): {Body}", model, quotaBody);
                return StatusCode(429, new { message = "AI service rate limit reached. Please wait a moment and try again." });
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Gemini API error {Status}: {Body}", response.StatusCode, errorBody);
                return StatusCode(502, new { message = "AI service returned an error. Please try again." });
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            rawText = responseBody;

            // Parse Gemini response and extract the text content
            using var geminiDoc = JsonDocument.Parse(responseBody);
            var candidate = geminiDoc.RootElement.GetProperty("candidates")[0];
            var text = candidate
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? "";

            rawText = text;

            if (candidate.TryGetProperty("finishReason", out var finishReasonProp) &&
                finishReasonProp.GetString() == "MAX_TOKENS")
            {
                _logger.LogWarning("Gemini response truncated by MAX_TOKENS. Partial text: {RawText}", rawText);
                return StatusCode(502, new { message = "AI response was too long and got cut off. Please try again." });
            }

            // Strip markdown code fences if Gemini wrapped the JSON
            text = text.Trim();
            if (text.StartsWith("```"))
            {
                var firstNewline = text.IndexOf('\n');
                var lastFence = text.LastIndexOf("```");
                if (firstNewline > 0 && lastFence > firstNewline)
                    text = text.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
            }

            // Validate the JSON and return it
            using var resultDoc = JsonDocument.Parse(text);
            var root = resultDoc.RootElement;

            // Safely extract each field with fallbacks
            string description = root.TryGetProperty("description", out var descProp) ? (descProp.GetString() ?? "") : "";
            decimal? amount = null;
            if (root.TryGetProperty("amount", out var amtProp))
            {
                if (amtProp.ValueKind == JsonValueKind.Number)
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

            // Validate ledgerCategory is one of the allowed values
            var validLedgerCategories = new[] { "Essentials", "Growth", "Stability", "Rewards", "Income" };
            if (!validLedgerCategories.Contains(ledgerCategory))
                ledgerCategory = "Essentials";

            return Ok(new
            {
                description,
                amount,
                date,
                category,
                ledgerCategory,
                txType,
                confidence
            });
        }
        catch (TaskCanceledException)
        {
            return StatusCode(504, new { message = "AI service timed out. Please try again." });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Gemini response as JSON. Raw text: {RawText}", rawText);
            return StatusCode(502, new { message = "Could not read the receipt. Please try a clearer photo.", rawResponse = rawText });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in OcrController.ScanReceipt");
            return StatusCode(500, new { message = "An unexpected error occurred. Please try again." });
        }
    }
}
