using FinancialAppApi.Services.Forecast;

namespace FinancialAppApi.Tests.Forecast;

public sealed class CashFlowForecastCalculatorTests
{
    private static readonly ForecastPeriod Current = new(new DateOnly(2026, 11, 23), new DateOnly(2026, 11, 27), "Rest of this cycle", true);
    private static readonly ForecastPeriod December = new(new DateOnly(2026, 11, 28), new DateOnly(2026, 12, 27), "Dec", false);
    private static readonly ForecastPeriod January = new(new DateOnly(2026, 12, 28), new DateOnly(2027, 1, 27), "Jan", false);

    private static CashFlowForecastInput Input(
        decimal? income = 4800m,
        decimal stabilityOpening = 0m,
        decimal stabilityTarget = 0m,
        decimal? everyday = null,
        decimal essentialsOpening = 0m,
        params ForecastItem[] items) => new(
        [Current, December, January],
        new Dictionary<string, decimal>
        {
            ["Essentials"] = essentialsOpening,
            ["Stability"] = stabilityOpening,
        },
        new ForecastAllocations(0.50m, 0.25m, 0.15m, 0.10m),
        stabilityTarget,
        "Growth 100%",
        income,
        everyday,
        items);

    [Fact]
    public void FutureCycles_ReceiveIncomeSplitByAllocation_ButTheCurrentCycleDoesNot()
    {
        var result = CashFlowForecastCalculator.Project(Input());

        Assert.Empty(result.Cycles[0].Lines);
        var december = result.Cycles[1];
        Assert.Equal(2400m, december.MoneyIn["Essentials"]);
        Assert.Equal(1200m, december.MoneyIn["Growth"]);
        Assert.Equal(720m, december.MoneyIn["Stability"]);
        Assert.Equal(480m, december.MoneyIn["Rewards"]);
        Assert.Equal(4800m, result.Cycles[2].Closing["Essentials"]);
        Assert.True(result.IncomeKnown);
    }

    [Fact]
    public void UnknownIncome_AddsNoIncomeAndIsReportedAsUnknown()
    {
        var result = CashFlowForecastCalculator.Project(Input(income: null));

        Assert.False(result.IncomeKnown);
        Assert.All(result.Cycles, cycle => Assert.Empty(cycle.Lines));
    }

    [Fact]
    public void ExplicitZero_IsKnownWithoutAddingAnyMovements()
    {
        var result = CashFlowForecastCalculator.Project(Input(income: 0m, everyday: 0m));

        Assert.True(result.IncomeKnown);
        Assert.True(result.EverydaySpendingIncluded);
        Assert.All(result.Cycles, cycle => Assert.Empty(cycle.Lines));
    }

    [Fact]
    public void StabilityReachingItsTarget_RedirectsTheSurplus_AndRecordsWhen()
    {
        // 9,500 + 720 would pass the 10,000 target, so only 500 goes to Stability in December
        // and the other 220 follows the "Growth 100%" redirect. January's share all goes to Growth.
        var result = CashFlowForecastCalculator.Project(Input(stabilityOpening: 9500m, stabilityTarget: 10000m));

        var december = result.Cycles[1];
        Assert.Equal(500m, december.MoneyIn["Stability"]);
        Assert.Equal(1420m, december.MoneyIn["Growth"]);
        Assert.True(december.StabilityTargetReached);
        Assert.Equal(December.Start, result.StabilityTargetReachedOn);

        var january = result.Cycles[2];
        Assert.Equal(0m, january.MoneyIn["Stability"]);
        Assert.Equal(1920m, january.MoneyIn["Growth"]);
    }

    [Fact]
    public void ATargetAlreadyMet_IsNotReportedAsANewMilestone()
    {
        var result = CashFlowForecastCalculator.Project(Input(stabilityOpening: 12000m, stabilityTarget: 10000m));

        Assert.Null(result.StabilityTargetReachedOn);
        Assert.All(result.Cycles, cycle => Assert.True(cycle.StabilityTargetReached));
    }

    [Fact]
    public void BillsThatPushABucketBelowZero_AreFlaggedAsShort()
    {
        var crown = new ForecastItem(new DateOnly(2026, 11, 25), -1800m, "Essentials", "Tooth crown", ForecastItemKinds.Scenario);

        var result = CashFlowForecastCalculator.Project(Input(essentialsOpening: 1000m, items: crown));

        Assert.Equal(-800m, result.Cycles[0].Closing["Essentials"]);
        Assert.Equal(["Essentials"], result.Cycles[0].ShortBuckets);
        Assert.Empty(result.Cycles[1].ShortBuckets);
    }

    [Fact]
    public void ExtraIncome_IsSplitLikeASalary_OnItsOwnDate()
    {
        var rebate = new ForecastItem(new DateOnly(2027, 1, 5), 915m, null, "Trade-in rebate", ForecastItemKinds.Scenario);

        var result = CashFlowForecastCalculator.Project(Input(income: null, items: rebate));

        var lines = result.Cycles[2].Lines;
        Assert.All(lines, line => Assert.Equal("Trade-in rebate", line.Label));
        Assert.Equal(915m, lines.Sum(line => line.Amount));
        Assert.Equal(457.50m, result.Cycles[2].MoneyIn["Essentials"]);
    }

    [Fact]
    public void EverydaySpending_ComesOutOfEssentials_InFutureCyclesOnly()
    {
        var result = CashFlowForecastCalculator.Project(Input(everyday: 600m));

        Assert.Equal(0m, result.Cycles[0].MoneyOut["Essentials"]);
        Assert.Equal(600m, result.Cycles[1].MoneyOut["Essentials"]);
        Assert.Equal(1800m, result.Cycles[1].Closing["Essentials"]);
        Assert.True(result.EverydaySpendingIncluded);
    }

    [Fact]
    public void IncomeSplit_KeepsEveryCent()
    {
        var result = CashFlowForecastCalculator.Project(Input(income: 1000.01m));

        Assert.Equal(1000.01m, result.Cycles[1].MoneyIn.Values.Sum());
    }
}
