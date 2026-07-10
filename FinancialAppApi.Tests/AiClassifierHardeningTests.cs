using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

// Phase 3: application-side validation of untrusted classifier output.
public class AiClassifierHardeningTests
{
    [Fact]
    public void UnknownIntents_AreRejected()
    {
        var result = AiAssistantService.SanitizeClassification(
            ["ledger.spending_total", "totally.fake.intent"], 0.8, null, null);
        Assert.NotNull(result);
        Assert.Equal(["ledger.spending_total"], result!.Intents);
    }

    [Fact]
    public void AllUnknownIntents_ReturnNull()
    {
        Assert.Null(AiAssistantService.SanitizeClassification(["nope", "still.nope"], 0.9, null, null));
    }

    [Theory]
    [InlineData(5.0, 1.0)]
    [InlineData(-2.0, 0.0)]
    [InlineData(double.NaN, 0.0)]
    public void Confidence_IsClamped(double raw, double expected)
    {
        var result = AiAssistantService.SanitizeClassification(["general"], raw, null, null);
        Assert.Equal(expected, result!.Confidence);
    }

    [Fact]
    public void SearchText_WhitespaceNormalizedAndLengthClamped()
    {
        var result = AiAssistantService.SanitizeClassification(["ledger.merchant_search"], 0.8, "   star   bucks   ", null);
        Assert.Equal("star bucks", result!.SearchText);

        var longText = new string('a', 200);
        var clamped = AiAssistantService.SanitizeClassification(["ledger.merchant_search"], 0.8, longText, null);
        Assert.True(clamped!.SearchText!.Length <= 80);
    }

    [Fact]
    public void EmptyIntents_ReturnNull()
    {
        Assert.Null(AiAssistantService.SanitizeClassification([], 0.9, "x", null));
    }
}
