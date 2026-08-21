using System.Text.Json;

namespace FinancialAppApi.Services.Documents;

public sealed record VaultAmountExtraction(decimal? Amount, string Currency, decimal? Confidence, string Status, string? Message);

public class VaultAmountExtractor
{
    private static readonly object AmountSchema = new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["amount"] = new { type = new[] { "number", "null" } },
            ["currency"] = new { type = "string", @enum = new[] { "MYR", "OTHER", "UNKNOWN" } },
            ["confidence"] = new { type = "number", minimum = 0, maximum = 1 }
        },
        required = new[] { "amount", "currency", "confidence" },
        additionalProperties = false
    };

    private readonly AiClient _aiClient;
    private readonly ILogger<VaultAmountExtractor> _logger;

    public VaultAmountExtractor(AiClient aiClient, ILogger<VaultAmountExtractor> logger)
    {
        _aiClient = aiClient;
        _logger = logger;
    }

    public virtual async Task<VaultAmountExtraction> ExtractAsync(
        string fileName,
        string mimeType,
        byte[] data,
        CancellationToken ct = default)
    {
        if (!_aiClient.IsConfigured)
            return new(null, "MYR", null, "Unavailable", "AI amount extraction is not configured.");

        var base64 = Convert.ToBase64String(data);
        var part = mimeType.StartsWith("image/", StringComparison.Ordinal)
            ? AiPart.FromImage(mimeType, base64)
            : AiPart.FromFile(fileName, mimeType, base64);

        try
        {
            var text = await _aiClient.GenerateTextAsync(
                [
                    part,
                    AiPart.FromText("Read this Malaysian tax-supporting document and extract the final amount actually paid. Return null when no reliable payable total is present.")
                ],
                new AiGenerationOptions(
                    Feature: "vault-amount-extraction",
                    Temperature: 0,
                    MaxOutputTokens: 120,
                    SystemInstruction: "Extract only evidence visible in the supplied document. Never calculate or invent a total. Amount must be non-negative. Use MYR only when RM or MYR is shown or the document clearly establishes Malaysian ringgit. The result is a suggestion that a person will review.",
                    OutputJsonSchema: AmountSchema,
                    ThinkingLevel: "low",
                    ModelConfigurationKey: "OpenAiModels:ReceiptOcr"),
                ct);

            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            decimal? amount = root.TryGetProperty("amount", out var amountValue) &&
                              amountValue.ValueKind == JsonValueKind.Number
                ? Math.Abs(amountValue.GetDecimal())
                : null;
            var currency = root.TryGetProperty("currency", out var currencyValue)
                ? currencyValue.GetString()?.ToUpperInvariant()
                : null;
            // UNKNOWN is one of the three answers the schema allows, and it is not a currency.
            // Nowhere else does this app substitute a real value for an unknown one: an
            // off-vocabulary AI intent is dropped rather than mapped to a real one, an unrecognised
            // category disqualifies a suggested record from being applied, and the branch below
            // already leaves an amount the model could not read absent. Calling it MYR read a figure
            // off a document that never claimed ringgit — and since the row renders in the app's own
            // currency, nothing on screen would have contradicted it.
            var isKnownCurrency = currency is "MYR" or "OTHER";
            decimal confidence = root.TryGetProperty("confidence", out var confidenceValue) &&
                                 confidenceValue.TryGetDecimal(out var parsedConfidence)
                ? Math.Clamp(parsedConfidence, 0, 1)
                : 0;

            // A number with no currency behind it is not a usable amount, so it takes the same
            // branch as no number at all. The stored currency is inert once the amount is absent;
            // it stays MYR only because the column cannot be empty.
            if (!amount.HasValue)
            {
                return new(null, isKnownCurrency ? currency! : "MYR", confidence, "NotFound",
                    "No reliable amount was found. Add it manually if needed.");
            }

            return isKnownCurrency
                ? new(amount.Value, currency!, confidence, "NeedsReview", null)
                : new(null, "MYR", confidence, "NotFound",
                    "An amount was read, but the document does not say which currency it is in. Add it manually if needed.");
        }
        catch (Exception ex) when (ex is AiClientException or JsonException or FormatException)
        {
            _logger.LogWarning(ex, "AI amount extraction failed for a vault document.");
            return new(null, "MYR", null, "Failed", ex.Message);
        }
    }
}
