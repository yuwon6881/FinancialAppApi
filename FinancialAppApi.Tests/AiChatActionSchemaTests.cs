using System.Text.Json;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

// The chat model can only emit payload fields the structured-output schema declares. A field that
// the dispatcher reads but the schema omits fails silently -- the model has no way to express it,
// so the action arrives with the field missing and the app appears to ignore the request. These
// tests pin the fields the AI UI actions actually depend on.
public class AiChatActionSchemaTests
{
    private static JsonElement ChatSchemaRoot()
    {
        var json = JsonSerializer.Serialize(AiResponseSchemas.Chat(["Food", "Transport"]));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static JsonElement ActionPayloadProperties(JsonElement root) =>
        root.GetProperty("properties").GetProperty("actions")
            .GetProperty("items").GetProperty("properties").GetProperty("payload")
            .GetProperty("properties");

    [Theory]
    // Ledger advanced-filter fields.
    [InlineData("minAmount")]
    [InlineData("maxAmount")]
    [InlineData("recurringOnly")]
    [InlineData("wishlistOnly")]
    [InlineData("startDate")]
    [InlineData("endDate")]
    // Per-subscription payment-reminder fields.
    [InlineData("enabled")]
    [InlineData("reminderMode")]
    [InlineData("leadDays")]
    public void ActionPayload_DeclaresEveryDispatchableField(string field)
    {
        Assert.True(ActionPayloadProperties(ChatSchemaRoot()).TryGetProperty(field, out _));
    }

    [Fact]
    public void ChatSchema_OffersEveryAllowedActionType()
    {
        var declared = ChatSchemaRoot()
            .GetProperty("properties").GetProperty("actions")
            .GetProperty("items").GetProperty("properties").GetProperty("type")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("updateRecurringReminder", declared);
        Assert.Contains("toggleRecurring", declared);
    }

    [Fact]
    public void ReminderMode_IsConstrainedToTheModesTheCardOffers()
    {
        var modes = ActionPayloadProperties(ChatSchemaRoot())
            .GetProperty("reminderMode").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()).ToList();

        Assert.Equal(["Once", "Daily"], modes);
    }
}
