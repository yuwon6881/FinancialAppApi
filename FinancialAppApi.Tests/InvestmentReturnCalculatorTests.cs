using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests;

public sealed class InvestmentReturnCalculatorTests
{
    [Fact]
    public void Calculate_ReturnsTenPercentForOneYearGrowth()
    {
        var result = InvestmentReturnCalculator.Calculate([
            new DatedInvestmentFlow(new DateOnly(2025, 1, 1), -1000m),
            new DatedInvestmentFlow(new DateOnly(2026, 1, 1), 1100m)
        ]);

        Assert.NotNull(result);
        Assert.InRange(result.Value, 0.09999m, 0.10001m);
    }

    [Fact]
    public void Calculate_AccountsForLateContributionsAndWithdrawals()
    {
        var result = InvestmentReturnCalculator.Calculate([
            new DatedInvestmentFlow(new DateOnly(2025, 1, 1), -1000m),
            new DatedInvestmentFlow(new DateOnly(2025, 12, 1), -1000m),
            new DatedInvestmentFlow(new DateOnly(2026, 1, 1), 2100m)
        ]);

        Assert.NotNull(result);
        Assert.True(result > 0.05m);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0.00000001")]
    public void Calculate_IsIndependentOfCurrencyScale(string amount)
    {
        var principal = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
        var start = new DateOnly(2024, 1, 1);
        var result = InvestmentReturnCalculator.Calculate([
            new DatedInvestmentFlow(start, -principal),
            new DatedInvestmentFlow(start.AddDays(365), principal * 0.1m),
            new DatedInvestmentFlow(start.AddDays(730), principal * 1.1m)
        ]);

        Assert.NotNull(result);
        Assert.InRange(result.Value, 0.09999m, 0.10001m);
    }

    [Fact]
    public void Calculate_ReturnsExactlyZeroForBreakEven()
    {
        var start = new DateOnly(2025, 1, 1);
        var result = InvestmentReturnCalculator.Calculate([
            new DatedInvestmentFlow(start, -1000m),
            new DatedInvestmentFlow(start.AddDays(365), 1000m)
        ]);

        Assert.Equal(0m, result);
    }

    [Fact]
    public void Calculate_RefusesFlowsWithoutAUsableReturn()
    {
        Assert.Null(InvestmentReturnCalculator.Calculate([]));
        Assert.Null(InvestmentReturnCalculator.Calculate([
            new DatedInvestmentFlow(new DateOnly(2025, 1, 1), -1000m)
        ]));
        Assert.Null(InvestmentReturnCalculator.Calculate([
            new DatedInvestmentFlow(new DateOnly(2025, 1, 1), -1000m),
            new DatedInvestmentFlow(new DateOnly(2025, 1, 1), 1100m)
        ]));
    }

    [Fact]
    public void Calculate_RefusesAmbiguousMultipleReturns()
    {
        var result = InvestmentReturnCalculator.Calculate([
            new DatedInvestmentFlow(new DateOnly(2023, 1, 1), -1000m),
            new DatedInvestmentFlow(new DateOnly(2024, 1, 1), 1500m),
            new DatedInvestmentFlow(new DateOnly(2025, 1, 1), -700m),
            new DatedInvestmentFlow(new DateOnly(2026, 1, 1), 100m)
        ]);

        Assert.Null(result);
    }
}
