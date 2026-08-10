using FinancialAppApi.Models;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

public class CategoryAttributionServiceTests
{
    [Fact]
    public void GetCycleRange_LabelsBothYearsWhenCycleCrossesNewYear()
    {
        var (_, _, label) = CategoryAttributionService.GetCycleRange(2026, 12, 28);

        Assert.Equal("Dec 28th, 2026 ~ Jan 27th, 2027", label);
    }

    [Fact]
    public void GetCycleRange_ClampsAdjacentStartsWithoutLeavingGaps()
    {
        var february = CategoryAttributionService.GetCycleRange(2026, 2, 31);
        var march = CategoryAttributionService.GetCycleRange(2026, 3, 31);

        Assert.Equal(new DateTime(2026, 2, 28), february.start);
        Assert.Equal(new DateTime(2026, 3, 30), february.end);
        Assert.Equal(february.end.AddDays(1), march.start);
    }

    [Fact]
    public void GetCategoryAmount_ReturnsAmountForPlainCategoryMatch()
    {
        var tx = new Transaction { LedgerCategory = "Essentials", Amount = -25.50m };

        var amount = CategoryAttributionService.GetCategoryAmount(tx, "Essentials");

        Assert.Equal(-25.50m, amount);
    }

    [Theory]
    [InlineData("Essentials", 500)]
    [InlineData("Growth", 250)]
    [InlineData("Stability", 150)]
    [InlineData("Rewards", 100)]
    public void GetCategoryAmount_AppliesIncomeSplitPercentage(string category, decimal expected)
    {
        var tx = new Transaction { LedgerCategory = "IncomeSplit:50,25,15,10", Amount = 1000m };

        var amount = CategoryAttributionService.GetCategoryAmount(tx, category);

        Assert.Equal(expected, amount);
    }

    [Theory]
    [InlineData("Essentials", -75)]
    [InlineData("Rewards", 75)]
    public void GetCategoryAmount_AppliesTransferDirectionForBothSides(string category, decimal expected)
    {
        var tx = new Transaction { LedgerCategory = "Transfer:Essentials->Rewards", Amount = 75m };

        var amount = CategoryAttributionService.GetCategoryAmount(tx, category);

        Assert.Equal(expected, amount);
    }

    [Fact]
    public void GetCategoryAmount_ReturnsZeroForNoMatch()
    {
        var tx = new Transaction { LedgerCategory = "Growth", Amount = 25m };

        var amount = CategoryAttributionService.GetCategoryAmount(tx, "Rewards");

        Assert.Equal(0m, amount);
    }
}
