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
    private readonly AiClient _aiClient;
    private readonly AppDbContext _context;
    private readonly TransactionCategoryService _categoryService;
    private readonly ILogger<ReceiptScanProcessor> _logger;

    public ReceiptScanProcessor(
        AiClient aiClient,
        AppDbContext context,
        TransactionCategoryService categoryService,
        ILogger<ReceiptScanProcessor> logger)
    {
        _aiClient = aiClient;
        _context = context;
        _categoryService = categoryService;
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

    private static readonly string[] ValidLedgerCategories = ["Essentials", "Growth", "Stability", "Rewards", "Income"];

    private const string ScanSystemInstruction = @"You are a receipt/invoice OCR assistant for a personal finance app.
Analyze the receipt image and extract the following fields. Return ONLY valid JSON, no markdown, no explanation.

JSON schema:
{
  ""description"": ""<merchant name or brief description of purchase, e.g. 'McDonald's', 'Grab Ride', 'Electricity Bill'>"",
  ""amount"": <numeric value, positive number, e.g. 24.50 - extract the TOTAL amount paid>,
  ""date"": ""<ISO date string YYYY-MM-DD if visible on receipt, otherwise null>"",
  ""category"": ""<best-fit category, MUST be one of the Available categories listed below (fallback to 'Other')>"",
  ""ledgerCategory"": ""<best-fit ledger category - MUST be one of: Essentials, Growth, Stability, Rewards, Income>"",
  ""txType"": ""outflow"",
  ""confidence"": <0.0 to 1.0 indicating how confident you are in the extracted data>
}

Rules:
- description: use the merchant/store name if visible; otherwise describe the purchase type
- amount: extract the final TOTAL amount (after tax/tip if applicable); return as a plain, non-negative number
- date: only return a date if you can clearly read it on the receipt; otherwise null
- category: pick the single most fitting category from the Available categories list below. Do not make up your own category name.
- ledgerCategory: pick the single most fitting ledger category (Essentials is typically for food/transport/bills, Rewards for entertainment/shopping, Growth for investments/education, Stability for savings/insurance, Income for salary/inflows).
- txType: always ""outflow"" for receipts (receipts are purchases)
- If you cannot read the receipt clearly, still return your best guess with a low confidence score

Return only the JSON object.";

    private async Task<ReceiptScanResult> ScanImageAsync(string base64Image, string mimeType)
    {
        if (!_aiClient.IsConfigured)
        {
            throw new ReceiptScanUserException("OCR service is not configured. Ask your administrator to set the AiApiKey.");
        }

        var categories = (await _categoryService.GetCategoriesAsync())
            .Select(c => c.Name)
            .OrderBy(name => name)
            .ToList();

        var content = $"Available categories JSON array: {JsonSerializer.Serialize(categories)}";

        string text;
        try
        {
            text = await _aiClient.GenerateTextAsync(
                [AiPart.FromImage(mimeType, base64Image), AiPart.FromText(content)],
                0.1,
                1024,
                ScanSystemInstruction);
        }
        catch (AiClientException ex)
        {
            throw new ReceiptScanUserException(ex.Message);
        }

        using var resultDoc = JsonDocument.Parse(text);
        var root = resultDoc.RootElement;

        string description = root.TryGetProperty("description", out var descProp) ? (descProp.GetString() ?? "") : "";
        decimal? amount = null;
        if (root.TryGetProperty("amount", out var amtProp) && amtProp.ValueKind == JsonValueKind.Number)
        {
            amount = Math.Abs(amtProp.GetDecimal());
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

        if (!ValidLedgerCategories.Contains(ledgerCategory))
        {
            ledgerCategory = "Essentials";
        }

        return new ReceiptScanResult(description, amount, date, category, ledgerCategory, txType, confidence);
    }

    private sealed class ReceiptScanUserException : Exception
    {
        public ReceiptScanUserException(string message) : base(message)
        {
        }
    }
}
