using FinancialAppApi.Services.Accounts;

namespace FinancialAppApi.Tests;

public sealed class LedgerAccountPlacementTests
{
    [Theory]
    [InlineData("Essentials", true, "Essentials")]
    [InlineData("Growth", true, "Growth")]
    [InlineData("Stability", true, "Stability")]
    [InlineData("Rewards", true, "Rewards")]
    [InlineData("essentials", true, "Essentials")]
    [InlineData("GROWTH", true, "Growth")]
    [InlineData("  Stability  ", true, "Stability")]
    [InlineData("rewards ", true, "Rewards")]
    [InlineData(null, false, null)]
    [InlineData("", false, null)]
    [InlineData("   ", false, null)]
    [InlineData("Food", false, null)]
    [InlineData("Income", false, null)]
    [InlineData("Transfer:Essentials->Rewards", false, null)]
    [InlineData("AccountMove", false, null)]
    [InlineData("UnknownBucket", false, null)]
    public void ValidatesAndNormalizesBucketCategories(
        string? input,
        bool expectedIsBucket,
        string? expectedBucket)
    {
        var isBucket = LedgerAccountPlacement.IsBucket(input);
        var bucket = LedgerAccountPlacement.BucketOrNull(input);

        Assert.Equal(expectedIsBucket, isBucket);
        Assert.Equal(expectedBucket, bucket);
    }
}
