using System.Globalization;
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
            var outcome = await ScanImageAsync(job.ImageBase64, job.MimeType);
            if (outcome.ErrorMessage != null)
            {
                _logger.LogWarning("Receipt scan job {JobId} failed with user-facing error: {Message}", jobId, outcome.ErrorMessage);
                await MarkFailed(job, outcome.ErrorMessage);
                return;
            }

            job.Status = "completed";
            job.ResultJson = JsonSerializer.Serialize(outcome.Result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            job.ErrorMessage = null;
            job.ImageBase64 = null;
            job.CompletedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
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

    private const string ScanSystemInstruction = @"Extract one receipt or invoice for a personal finance app.
Rules:
- description: use the merchant/store name if visible; otherwise describe the purchase type
- amount: extract the final TOTAL amount (after tax/tip if applicable); return as a plain, non-negative number
- date: only return a date if you can clearly read it on the receipt; otherwise null
- category: pick the single most fitting category from the available list. Do not invent a category.
- ledgerCategory: pick the single most fitting ledger category (Essentials is typically for food/transport/bills, Rewards for entertainment/shopping, Growth for investments/education, Stability for savings/insurance, Income for salary/inflows).
- If critical text is unclear, return the best supported value with low confidence; never invent receipt details.";

    private async Task<ScanOutcome> ScanImageAsync(string base64Image, string mimeType)
    {
        if (!_aiClient.IsConfigured)
        {
            return ScanOutcome.Failed("OCR service is not configured. Ask your administrator to set the AiApiKey.");
        }

        var categories = (await _categoryService.GetCategoriesAsync())
            .Select(c => c.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name)
            .ToList();
        if (categories.Count == 0)
        {
            return ScanOutcome.Failed("No transaction categories are configured.");
        }

        var content = $"Available categories JSON array: {JsonSerializer.Serialize(categories)}";

        string text;
        try
        {
            text = await _aiClient.GenerateTextAsync(
                [AiPart.FromImage(mimeType, base64Image), AiPart.FromText(content)],
                new AiGenerationOptions(
                    Feature: "receipt-ocr",
                    Temperature: 0,
                    MaxOutputTokens: 260,
                    SystemInstruction: ScanSystemInstruction,
                    ResponseJsonSchema: AiResponseSchemas.Receipt(categories),
                    ThinkingLevel: "low",
                    ModelConfigurationKey: "AiModels:ReceiptOcr",
                    FallbackModelConfigurationKey: "AiFallbackModels:ReceiptOcr"));
        }
        catch (AiClientException ex)
        {
            return ScanOutcome.Failed(ex.Message);
        }

        using var resultDoc = JsonDocument.Parse(text);
        var root = resultDoc.RootElement;

        string description = root.TryGetProperty("description", out var descProp) ? (descProp.GetString() ?? "").Trim() : "";
        if (description.Length > 120) description = description[..120].Trim();
        decimal? amount = null;
        if (root.TryGetProperty("amount", out var amtProp) && amtProp.ValueKind == JsonValueKind.Number)
        {
            amount = Math.Abs(amtProp.GetDecimal());
        }

        string? date = null;
        if (root.TryGetProperty("date", out var dateProp) && dateProp.ValueKind == JsonValueKind.String &&
            DateOnly.TryParseExact(dateProp.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            date = parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        var rawCategory = root.TryGetProperty("category", out var catProp) ? catProp.GetString() : null;
        string category = categories.FirstOrDefault(c => string.Equals(c, rawCategory, StringComparison.OrdinalIgnoreCase))
            ?? categories.FirstOrDefault(c => string.Equals(c, "Other", StringComparison.OrdinalIgnoreCase))
            ?? categories[0];
        var rawLedgerCategory = root.TryGetProperty("ledgerCategory", out var lcProp) ? lcProp.GetString() : null;
        string ledgerCategory = ValidLedgerCategories.FirstOrDefault(c => string.Equals(c, rawLedgerCategory, StringComparison.OrdinalIgnoreCase))
            ?? "Essentials";
        double confidence = root.TryGetProperty("confidence", out var confProp) && confProp.ValueKind == JsonValueKind.Number
            ? Math.Clamp(confProp.GetDouble(), 0, 1)
            : 0.5;
        if (string.IsNullOrWhiteSpace(description) && amount == null)
        {
            return ScanOutcome.Failed("Could not read a merchant or total from the receipt. Please try a clearer photo.");
        }

        return ScanOutcome.Ok(new ReceiptScanResult(description, amount, date, category, ledgerCategory, "outflow", confidence));
    }

    private sealed record ScanOutcome(ReceiptScanResult? Result, string? ErrorMessage)
    {
        public static ScanOutcome Ok(ReceiptScanResult result) => new(result, null);
        public static ScanOutcome Failed(string message) => new(null, message);
    }
}
