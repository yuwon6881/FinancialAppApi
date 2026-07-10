using FinancialAppApi.Services;
using DS = FinancialAppApi.Services.AiAssistantService.AiDatasetState;
using Status = FinancialAppApi.Services.AiAssistantService.AiDatasetStatus;

namespace FinancialAppApi.Tests;

// Phase 5: sufficiency gate over typed dataset statuses.
public class AiSufficiencyValidatorTests
{
    private static Dictionary<AiDatasetKey, DS> Data(params (AiDatasetKey Key, DS State)[] entries)
        => entries.ToDictionary(e => e.Key, e => e.State);

    [Fact]
    public void VerifiedEmpty_IsAnswerable_NotMissing()
    {
        var result = AiAssistantService.EvaluateSufficiency(
            ["ledger.spending_total"],
            Data((AiDatasetKey.CycleSummaries, new DS(Status.VerifiedEmpty))));
        Assert.True(result.CanAnswer);
        Assert.Empty(result.Missing);
    }

    [Fact]
    public void MissingDataset_IsNotAnswerable()
    {
        var result = AiAssistantService.EvaluateSufficiency(
            ["ledger.spending_total"],
            Data());
        Assert.False(result.CanAnswer);
        Assert.Single(result.Missing);
    }

    [Fact]
    public void SensitiveHidden_Blocks()
    {
        var result = AiAssistantService.EvaluateSufficiency(
            ["wishlist.forecast"],
            Data((AiDatasetKey.WishlistForecast, new DS(Status.Hidden, Reason: "hidden by sensitive mode"))));
        Assert.False(result.CanAnswer);
        Assert.Contains(result.Missing, m => m.DatasetKey == AiDatasetKey.WishlistForecast);
    }

    [Fact]
    public void Unavailable_Blocks()
    {
        var result = AiAssistantService.EvaluateSufficiency(
            ["wishlist.forecast"],
            Data((AiDatasetKey.WishlistForecast, new DS(Status.Unavailable))));
        Assert.False(result.CanAnswer);
    }

    [Fact]
    public void TruncatedExactIntentWithoutExactMetric_IsApproximateAndRecoverable()
    {
        // e.g. > 2000 (or exactly 2000) rows in a cycle: aggregate is truncated.
        var result = AiAssistantService.EvaluateSufficiency(
            ["ledger.spending_total"],
            Data((AiDatasetKey.CycleSummaries, new DS(Status.Truncated, TotalCount: 2000, HasExactMetric: false))));
        Assert.True(result.CanAnswer);
        Assert.True(result.IsApproximate);
        Assert.Contains(result.Recoverable, r => r.DatasetKey == AiDatasetKey.CycleSummaries);
    }

    [Fact]
    public void ExactCountWithBoundedSample_IsNotApproximate()
    {
        // 120-row prompt sample but an exact COUNT was recovered -> precise answer allowed.
        var result = AiAssistantService.EvaluateSufficiency(
            ["ledger.activity_count"],
            Data((AiDatasetKey.TransactionMatches, new DS(Status.Truncated, TotalCount: 4, IncludedCount: 120, HasExactMetric: true))));
        Assert.True(result.CanAnswer);
        Assert.False(result.IsApproximate);
    }

    [Fact]
    public void TruncatedNonExactIntent_IsApproximateButAnswerable()
    {
        var result = AiAssistantService.EvaluateSufficiency(
            ["ledger.merchant_search"],
            Data((AiDatasetKey.TransactionMatches, new DS(Status.Truncated))));
        Assert.True(result.CanAnswer);
        Assert.True(result.IsApproximate);
    }

    [Theory]
    [InlineData("You spent 1234 this cycle.", true, "Approximately: You spent 1234 this cycle.")]
    [InlineData("You spent about 1234 this cycle.", true, "You spent about 1234 this cycle.")]
    [InlineData("You spent 1234 this cycle.", false, "You spent 1234 this cycle.")]
    public void ApproximateWording_IsEnforcedOnlyWhenNeeded(string reply, bool approximate, string expected)
    {
        Assert.Equal(expected, InvokeEnforce(reply, approximate));
    }

    // EnforceApproximateWording is private; exercise it via reflection to keep it internal.
    private static string InvokeEnforce(string reply, bool approximate)
    {
        var method = typeof(AiAssistantService).GetMethod(
            "EnforceApproximateWording",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (string)method.Invoke(null, [reply, approximate])!;
    }
}
