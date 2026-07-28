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

public sealed record ReceiptSplitItem(
    string Name,
    decimal Quantity,
    decimal? UnitPrice,
    decimal? LineTotal,
    double Confidence);

public sealed record ReceiptSplitCharge(
    string Label,
    string Kind,
    string Operation,
    string Basis,
    decimal? Amount,
    decimal? RatePercent,
    int Sequence,
    IReadOnlyList<int> EligibleItemIndexes,
    double Confidence);

public sealed record ReceiptSplitFieldConfidence(
    double Description,
    double Date,
    double Currency,
    double Subtotal,
    double Total);

public sealed record ReceiptSplitScanResult(
    string Description,
    string? Date,
    string? Currency,
    decimal? Subtotal,
    decimal? Total,
    string Category,
    string LedgerCategory,
    IReadOnlyList<ReceiptSplitItem> Items,
    IReadOnlyList<ReceiptSplitCharge> Charges,
    ReceiptSplitFieldConfidence FieldConfidence,
    bool Truncated,
    IReadOnlyList<string> Warnings,
    double Confidence);

public record InvestmentActivityScanResult(
    string? Type,
    Guid? AccountId,
    Guid? InstrumentId,
    string? TradeDate,
    decimal? Units,
    decimal? UnitPrice,
    decimal? CashAmount,
    decimal? Fees,
    decimal? Taxes,
    double Confidence);

public enum ReceiptScanProcessStatus
{
    Processed,
    AlreadyFinished,
    InProgress,
    NotFound
}

public class ReceiptScanProcessor
{
    public static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(2);
    private readonly AiClient _aiClient;
    private readonly AppDbContext _context;
    private readonly IReceiptImageStore _imageStore;
    private readonly TransactionCategoryService _categoryService;
    private readonly ILogger<ReceiptScanProcessor> _logger;

    public ReceiptScanProcessor(
        AiClient aiClient,
        AppDbContext context,
        IReceiptImageStore imageStore,
        TransactionCategoryService categoryService,
        ILogger<ReceiptScanProcessor> logger)
    {
        _aiClient = aiClient;
        _context = context;
        _imageStore = imageStore;
        _categoryService = categoryService;
        _logger = logger;
    }

    public async Task<ReceiptScanProcessStatus> ProcessAsync(string jobId)
    {
        ReceiptScanJob? job;
        if (_context.Database.IsRelational())
        {
            var claimedAt = DateTime.UtcNow;
            var staleBefore = claimedAt - ProcessingLease;
            var claimed = await _context.ReceiptScanJobs
                .Where(j => j.Id == jobId &&
                    (j.Status == "queued" || (j.Status == "processing" && j.UpdatedAt < staleBefore)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(j => j.Status, "processing")
                    .SetProperty(j => j.UpdatedAt, claimedAt));
            if (claimed == 0)
            {
                var existingStatus = await _context.ReceiptScanJobs
                    .AsNoTracking()
                    .Where(j => j.Id == jobId)
                    .Select(j => j.Status)
                    .FirstOrDefaultAsync();
                return existingStatus switch
                {
                    null => ReceiptScanProcessStatus.NotFound,
                    "completed" or "failed" => ReceiptScanProcessStatus.AlreadyFinished,
                    _ => ReceiptScanProcessStatus.InProgress
                };
            }
            job = await _context.ReceiptScanJobs.FirstOrDefaultAsync(j => j.Id == jobId);
        }
        else
        {
            job = await _context.ReceiptScanJobs.FirstOrDefaultAsync(j => j.Id == jobId);
            if (job == null)
            {
                _logger.LogWarning("Receipt scan job {JobId} was not found.", jobId);
                return ReceiptScanProcessStatus.NotFound;
            }
            if (job.Status is "completed" or "failed") return ReceiptScanProcessStatus.AlreadyFinished;
            if (job.Status == "processing" && job.UpdatedAt >= DateTime.UtcNow - ProcessingLease)
                return ReceiptScanProcessStatus.InProgress;
            if (job.Status != "queued" && job.Status != "processing")
                return ReceiptScanProcessStatus.InProgress;
            job.Status = "processing";
            job.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        if (job == null)
        {
            _logger.LogWarning("Receipt scan job {JobId} was not found.", jobId);
            return ReceiptScanProcessStatus.NotFound;
        }

        // Worker requests are not authenticated HTTP requests. Once the job is claimed,
        // bind its owner so category lookups cannot see another user's categories.
        _context.SetCurrentUser(job.UserId);

        if (string.IsNullOrWhiteSpace(job.StorageObjectPath))
        {
            await MarkFailed(job, "Receipt image was not available for processing.");
            return ReceiptScanProcessStatus.Processed;
        }

        byte[]? imageData;
        try
        {
            imageData = await _imageStore.DownloadAsync(job.StorageObjectPath);
        }
        catch (ReceiptImageStoreException)
        {
            // Release the processing lease so Cloud Tasks (or the in-process
            // recovery loop) can retry a transient Storage API failure.
            job.Status = "queued";
            job.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            throw;
        }

        if (imageData is not { Length: > 0 })
        {
            await MarkFailed(job, "Receipt image was not available for processing.");
            return ReceiptScanProcessStatus.Processed;
        }

        try
        {
            var encodedImage = Convert.ToBase64String(imageData);
            var outcome = job.ScanType switch
            {
                "investment" => await ScanInvestmentImageAsync(encodedImage, job.MimeType),
                "receipt-split" => await ScanReceiptSplitImageAsync(encodedImage, job.MimeType),
                _ => await ScanReceiptImageAsync(encodedImage, job.MimeType)
            };
            if (outcome.ErrorMessage != null)
            {
                _logger.LogWarning("Receipt scan job {JobId} failed with user-facing error: {Message}", jobId, outcome.ErrorMessage);
                await MarkFailed(job, outcome.ErrorMessage);
                return ReceiptScanProcessStatus.Processed;
            }

            job.Status = "completed";
            job.ResultJson = JsonSerializer.Serialize(outcome.Result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            job.ErrorMessage = null;
            job.CompletedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            await TryDeleteTerminalImageAsync(job);
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

        return ReceiptScanProcessStatus.Processed;
    }

    private async Task MarkFailed(ReceiptScanJob job, string message)
    {
        job.Status = "failed";
        job.ErrorMessage = message;
        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await TryDeleteTerminalImageAsync(job);
    }

    private async Task TryDeleteTerminalImageAsync(ReceiptScanJob job)
    {
        if (string.IsNullOrWhiteSpace(job.StorageObjectPath)) return;

        try
        {
            await _imageStore.DeleteIfExistsAsync(job.StorageObjectPath);
        }
        catch (ReceiptImageStoreException exception)
        {
            // Completion is already durable. Keep the object path on the terminal
            // row so retention cleanup can retry without losing the reference.
            _logger.LogError(exception, "Could not delete terminal receipt image for OCR job {JobId}.", job.Id);
            return;
        }

        job.StorageObjectPath = null;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (Exception exception)
        {
            // The terminal status was saved before object deletion. If clearing the
            // now-stale path fails, cleanup treats a later Storage 404 as success.
            _logger.LogError(exception, "Could not clear the deleted receipt object path for OCR job {JobId}.", job.Id);
        }
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

    private const string ReceiptSplitScanSystemInstruction = @"Extract the visible structure of one receipt for a personal expense-share calculator.
Rules:
- Extract every visible purchasable line into items. Keep grouped quantities when printed.
- Do not include subtotal, total, tender, tax, service, tip, discount, or rounding rows as items.
- unitPrice and lineTotal must be non-negative. Return null when not visibly supported or directly derivable.
- Extract every receipt-level tax, service charge, tip, discount, rounding, and other adjustment into charges.
- amount is the printed absolute amount. ratePercent is the printed percentage as a number such as 10 for 10%.
- operation is add for added charges, subtract for discounts/negative adjustments, and included when already included in item prices.
- basis is runningTotal only when the receipt clearly applies the percentage after an earlier charge; otherwise subtotal.
- sequence follows the printed calculation order.
- eligibleItemIndexes contains zero-based item indexes only when the receipt clearly limits a charge to particular items; otherwise return an empty array to mean all items.
- subtotal and total are printed receipt values, not values you calculate.
- truncated is true when visible lines could not all be extracted. Add short warnings for unclear or ambiguous content.
- fieldConfidence reports confidence separately for merchant, date, currency, subtotal, and total.
- Never invent an item, amount, rate, charge rule, date, or currency. Use low confidence and null values for unclear fields.";

    private async Task<ScanOutcome> ScanReceiptImageAsync(string base64Image, string mimeType)
    {
        if (!_aiClient.IsConfigured)
        {
            return ScanOutcome.Failed("Receipt scanning is not configured for this app.");
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
                    ModelConfigurationKey: "AiModels:ReceiptOcr"));
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

    private async Task<ScanOutcome> ScanReceiptSplitImageAsync(string base64Image, string mimeType)
    {
        if (!_aiClient.IsConfigured)
            return ScanOutcome.Failed("Receipt splitting is not configured for this app.");

        var categories = (await _categoryService.GetCategoriesAsync())
            .Select(c => c.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name)
            .ToList();
        if (categories.Count == 0)
            return ScanOutcome.Failed("No transaction categories are configured.");

        string text;
        try
        {
            text = await _aiClient.GenerateTextAsync(
                [
                    AiPart.FromImage(mimeType, base64Image),
                    AiPart.FromText($"Available categories JSON array: {JsonSerializer.Serialize(categories)}")
                ],
                new AiGenerationOptions(
                    Feature: "receipt-split-ocr",
                    Temperature: 0,
                    MaxOutputTokens: 2500,
                    SystemInstruction: ReceiptSplitScanSystemInstruction,
                    ResponseJsonSchema: AiResponseSchemas.ReceiptSplit(categories),
                    ThinkingLevel: "low",
                    ModelConfigurationKey: "AiModels:ReceiptOcr"));
        }
        catch (AiClientException ex)
        {
            return ScanOutcome.Failed(ex.Message);
        }

        var result = JsonSerializer.Deserialize<ReceiptSplitScanResult>(text, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (result == null)
            return ScanOutcome.Failed("Could not read item details from the receipt. Please try a clearer photo.");

        var description = (result.Description ?? string.Empty).Trim();
        if (description.Length > 120) description = description[..120].Trim();
        string? date = null;
        if (DateOnly.TryParseExact(result.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedDate))
        {
            date = parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        var category = categories.FirstOrDefault(c =>
                string.Equals(c, result.Category, StringComparison.OrdinalIgnoreCase))
            ?? categories.FirstOrDefault(c => string.Equals(c, "Other", StringComparison.OrdinalIgnoreCase))
            ?? categories[0];
        var ledgerCategory = ValidLedgerCategories.FirstOrDefault(c =>
                string.Equals(c, result.LedgerCategory, StringComparison.OrdinalIgnoreCase))
            ?? "Essentials";
        var items = (result.Items ?? [])
            .Take(80)
            .Select(item => new ReceiptSplitItem(
                string.IsNullOrWhiteSpace(item.Name) ? "Unclear item" : item.Name.Trim()[..Math.Min(item.Name.Trim().Length, 120)],
                item.Quantity > 0 ? item.Quantity : 1,
                item.UnitPrice is >= 0 ? item.UnitPrice : null,
                item.LineTotal is >= 0 ? item.LineTotal : null,
                Math.Clamp(item.Confidence, 0, 1)))
            .ToList();
        var charges = (result.Charges ?? [])
            .Take(20)
            .Select((charge, index) => new ReceiptSplitCharge(
                string.IsNullOrWhiteSpace(charge.Label) ? "Other charge" : charge.Label.Trim()[..Math.Min(charge.Label.Trim().Length, 80)],
                charge.Kind is "tax" or "service" or "tip" or "discount" or "rounding" or "other"
                    ? charge.Kind : "other",
                charge.Operation is "add" or "subtract" or "included"
                    ? charge.Operation : charge.Kind == "discount" ? "subtract" : "add",
                charge.Basis == "runningTotal" ? "runningTotal" : "subtotal",
                charge.Amount is >= 0 ? charge.Amount : null,
                charge.RatePercent is >= 0 ? charge.RatePercent : null,
                charge.Sequence >= 0 ? charge.Sequence : index,
                (charge.EligibleItemIndexes ?? [])
                    .Where(itemIndex => itemIndex >= 0 && itemIndex < items.Count)
                    .Distinct()
                    .ToList(),
                Math.Clamp(charge.Confidence, 0, 1)))
            .OrderBy(charge => charge.Sequence)
            .ToList();
        var warnings = (result.Warnings ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Take(10)
            .ToList();
        var fieldConfidence = result.FieldConfidence ?? new ReceiptSplitFieldConfidence(0, 0, 0, 0, 0);

        if (items.Count == 0)
            warnings.Add("No line items were confidently extracted. Add them manually before calculating your share.");
        if (string.IsNullOrWhiteSpace(description) && items.Count == 0 && result.Total == null)
            return ScanOutcome.Failed("Could not read item details from the receipt. Please try a clearer photo.");

        return ScanOutcome.Ok(result with
        {
            Description = description,
            Date = date,
            Currency = string.IsNullOrWhiteSpace(result.Currency) ? null : result.Currency.Trim()[..Math.Min(result.Currency.Trim().Length, 12)],
            Subtotal = result.Subtotal is >= 0 ? result.Subtotal : null,
            Total = result.Total is >= 0 ? result.Total : null,
            Category = category,
            LedgerCategory = ledgerCategory,
            Items = items,
            Charges = charges,
            FieldConfidence = new ReceiptSplitFieldConfidence(
                Math.Clamp(fieldConfidence.Description, 0, 1),
                Math.Clamp(fieldConfidence.Date, 0, 1),
                Math.Clamp(fieldConfidence.Currency, 0, 1),
                Math.Clamp(fieldConfidence.Subtotal, 0, 1),
                Math.Clamp(fieldConfidence.Total, 0, 1)),
            Warnings = warnings,
            Confidence = Math.Clamp(result.Confidence, 0, 1)
        });
    }

    private const string InvestmentScanSystemInstruction = @"Extract one investment activity from a broker confirmation, statement, or screenshot.
Only these activity types are supported: Buy, Sell, Dividend, FeeTax.
Rules:
- Choose an accountId or instrumentId only from the supplied options and only when the image clearly supports the match.
- Use FeeTax for a standalone broker fee or tax charge, not fees/taxes attached to a buy, sell, or dividend.
- cashAmount is the positive gross trade amount, gross dividend, or standalone charge amount.
- fees and taxes are separate non-negative amounts.
- Return null for every field that is unclear, absent, or not applicable. Never guess.
- For dropdown fields, choose the single most confident supported option; otherwise return null.
- Dates must be YYYY-MM-DD and must be visibly supported by the image.";

    private async Task<ScanOutcome> ScanInvestmentImageAsync(string base64Image, string mimeType)
    {
        if (!_aiClient.IsConfigured)
            return ScanOutcome.Failed("Investment scanning is not configured for this app.");

        var accounts = await _context.InvestmentAccounts.AsNoTracking()
            .Where(value => !value.IsArchived)
            .OrderBy(value => value.Name)
            .Select(value => new { id = value.Id, name = value.Name, baseCurrency = value.BaseCurrency })
            .ToListAsync();
        var instruments = await _context.InvestmentInstruments.AsNoTracking()
            .Where(value => !value.IsArchived)
            .OrderBy(value => value.Symbol)
            .Select(value => new
            {
                id = value.Id,
                symbol = value.Symbol,
                name = value.Name,
                currency = value.Currency,
                exchange = value.Exchange
            })
            .ToListAsync();
        if (accounts.Count == 0 || instruments.Count == 0)
            return ScanOutcome.Failed("Add an investment account and investment before scanning activity.");

        var context = JsonSerializer.Serialize(new { accounts, instruments });
        string text;
        try
        {
            text = await _aiClient.GenerateTextAsync(
                [AiPart.FromImage(mimeType, base64Image), AiPart.FromText($"Available options JSON: {context}")],
                new AiGenerationOptions(
                    Feature: "investment-ocr",
                    Temperature: 0,
                    MaxOutputTokens: 280,
                    SystemInstruction: InvestmentScanSystemInstruction,
                    ResponseJsonSchema: AiResponseSchemas.InvestmentActivityScan,
                    ThinkingLevel: "low",
                    ModelConfigurationKey: "AiModels:ReceiptOcr"));
        }
        catch (AiClientException ex)
        {
            return ScanOutcome.Failed(ex.Message);
        }

        using var resultDoc = JsonDocument.Parse(text);
        var root = resultDoc.RootElement;
        string? ReadString(string name) =>
            root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        decimal? ReadNonNegative(string name, bool positive = false)
        {
            if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number)
                return null;
            var value = property.GetDecimal();
            return positive ? value > 0 ? value : null : value >= 0 ? value : null;
        }

        var rawType = ReadString("type");
        var type = InvestmentKinds.TransactionTypes.FirstOrDefault(value =>
            string.Equals(value, rawType, StringComparison.OrdinalIgnoreCase));
        Guid? accountId = Guid.TryParse(ReadString("accountId"), out var parsedAccount) &&
            accounts.Any(value => value.id == parsedAccount) ? parsedAccount : null;
        Guid? instrumentId = Guid.TryParse(ReadString("instrumentId"), out var parsedInstrument) &&
            instruments.Any(value => value.id == parsedInstrument) ? parsedInstrument : null;
        string? tradeDate = null;
        if (DateOnly.TryParseExact(ReadString("tradeDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedDate) &&
            parsedDate <= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
        {
            tradeDate = parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        var confidence = root.TryGetProperty("confidence", out var confidenceProperty) &&
            confidenceProperty.ValueKind == JsonValueKind.Number
                ? Math.Clamp(confidenceProperty.GetDouble(), 0, 1)
                : 0.5;
        var units = ReadNonNegative("units", positive: true);
        var unitPrice = ReadNonNegative("unitPrice", positive: true);
        var cashAmount = ReadNonNegative("cashAmount", positive: true);
        var fees = ReadNonNegative("fees");
        var taxes = ReadNonNegative("taxes");
        if (type == "FeeTax")
        {
            var charge = (fees ?? 0) + (taxes ?? 0);
            if (cashAmount is null && charge > 0) cashAmount = charge;
            fees = null;
            taxes = null;
        }

        var result = new InvestmentActivityScanResult(
            type,
            accountId,
            instrumentId,
            tradeDate,
            units,
            unitPrice,
            cashAmount,
            fees,
            taxes,
            confidence);
        if (result.Type is null && result.AccountId is null && result.InstrumentId is null &&
            result.TradeDate is null && result.Units is null && result.UnitPrice is null &&
            result.CashAmount is null && result.Fees is null && result.Taxes is null)
        {
            return ScanOutcome.Failed("Could not identify investment activity in this image. Please try a clearer image.");
        }

        return ScanOutcome.Ok(result);
    }

    private sealed record ScanOutcome(object? Result, string? ErrorMessage)
    {
        public static ScanOutcome Ok(object result) => new(result, null);
        public static ScanOutcome Failed(string message) => new(null, message);
    }
}
