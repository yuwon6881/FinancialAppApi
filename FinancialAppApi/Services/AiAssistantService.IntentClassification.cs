using System.Globalization;
using System.Text.Json;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private async Task<IntentClassification?> TryClassifyIntentAsync(
        string message,
        AiConversationState? priorState,
        CancellationToken cancellationToken)
    {
        // No prior dialogue is sent -- only a compact, canonical summary of the prior turn's
        // resolved frame (a few tokens) so the classifier can still interpret a short follow-up
        // ("how about last cycle") without shipping the previous message text back to the model.
        var classifierPrompt = $"Classify the user's financial-app request. Return only the JSON schema. " +
            $"Choose one or more intents, extract searchText for a merchant/activity, and preserve cycle wording. " +
            $"Treat shorthand, omitted nouns, abbreviations, and conversational equivalents by meaning: " +
            $"ledger.purchase_frequency covers how often the user buys, purchases, replaces, or restocks something; " +
            $"category_limits.analysis covers category budgets/caps/allowances and remaining room; " +
            $"cycle.insights covers a cycle/month recap, progress, health, update, 'so far', 'how am I tracking', " +
            $"or 'where do I stand', including an ongoing cycle that has no saved end-of-cycle summary. " +
            $"User message: {JsonSerializer.Serialize(message)} " +
            $"Prior request summary: {JsonSerializer.Serialize(SummarizePriorFrame(priorState))}";
        try
        {
            var text = await _aiClient.GenerateTextAsync(
                [AiPart.FromText(classifierPrompt)],
                new AiGenerationOptions(
                    Feature: "chat-intent-classification",
                    Temperature: 0,
                    MaxOutputTokens: 220,
                    OutputJsonSchema: AiResponseSchemas.IntentClassification,
                    ThinkingLevel: "none",
                    ModelConfigurationKey: "OpenAiModels:IntentClassifier"),
                cancellationToken);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var intents = root.TryGetProperty("intents", out var intentsElement) && intentsElement.ValueKind == JsonValueKind.Array
                ? intentsElement.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList()
                : [];
            var confidence = root.TryGetProperty("confidence", out var confidenceElement) && confidenceElement.TryGetDouble(out var value) ? value : 0;
            // searchText/cycleHint may arrive top-level (legacy) or under an "entities" object.
            var entities = root.TryGetProperty("entities", out var entitiesElement) && entitiesElement.ValueKind == JsonValueKind.Object
                ? entitiesElement
                : root;
            string? StringField(JsonElement parent, string name) =>
                parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
            var searchText = StringField(entities, "searchText") ?? StringField(root, "searchText");
            var cycleHint = StringField(entities, "cycleHint") ?? StringField(root, "cycleHint")
                ?? StringField(entities, "cycleReference") ?? StringField(entities, "cycleHint");
            var date = StringField(entities, "date") is { } dateText &&
                DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
                ? parsedDate : (DateOnly?)null;
            var category = StringField(entities, "category");
            var ledgerCategory = StringField(entities, "ledgerCategory");
            decimal? amount = null;
            if (entities.TryGetProperty("amount", out var amountElement) && amountElement.ValueKind == JsonValueKind.Number &&
                amountElement.TryGetDecimal(out var parsedAmount) && parsedAmount >= 0 && parsedAmount <= 1_000_000_000m)
            {
                amount = parsedAmount;
            }
            var wishlistReference = StringField(entities, "wishlistReference");
            var transactionReference = StringField(entities, "transactionReference");
            var ledgerAccountReference = StringField(entities, "ledgerAccountReference");
            var classifierConstraints = ParseClassifierConstraints(root);
            var ambiguities = root.TryGetProperty("ambiguities", out var ambiguityElement) && ambiguityElement.ValueKind == JsonValueKind.Array
                ? ambiguityElement.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : [];
            // SanitizeClassification rejects unknown intents, clamps confidence, and normalizes text.
            return SanitizeClassification(intents, confidence, searchText, cycleHint, date, category, ledgerCategory,
                amount, wishlistReference, transactionReference, classifierConstraints, ambiguities, ledgerAccountReference);
        }
        catch (Exception ex) when (ex is AiClientException or JsonException or FormatException)
        {
            return null;
        }
    }

    private static AiConstraints? ParseClassifierConstraints(JsonElement root)
    {
        if (!root.TryGetProperty("constraints", out var element) || element.ValueKind != JsonValueKind.Object) return null;
        bool Bool(string name) => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();
        var exclusions = element.TryGetProperty("exclusions", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)).Take(20).ToList()
            : [];
        return new AiConstraints(Bool("preventNavigation"), Bool("excludeTransfers"), exclusions, [], [], Bool("hypothetical"));
    }

}
