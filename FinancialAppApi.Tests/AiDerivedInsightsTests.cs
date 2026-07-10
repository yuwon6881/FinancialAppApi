using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

// Unit coverage for the three gap-filling derived insights: amount-threshold parsing/filtering,
// generic ledger-balance target forecasting, and frequency-normalized recurring cost.
public class AiDerivedInsightsTests
{
    private static AiAssistantService.AiTransactionRow Row(
        string id, int year, int month, int day, string description, decimal amount,
        string category = "Food", string ledger = "Essentials")
        => new(id, new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Utc),
            $"{year:D4}-{month:D2}-{day:D2}", description, category, ledger, amount);

    // ---------- amount threshold parsing ----------

    [Theory]
    [InlineData("which transaction exceeded 250 last cycle", "GreaterThan", 250)]
    [InlineData("purchases over 100", "GreaterThan", 100)]
    [InlineData("anything more than 1,500 this month", "GreaterThan", 1500)]
    [InlineData("transactions at least 300", "GreaterOrEqual", 300)]
    [InlineData("spending under 50", "LessThan", 50)]
    [InlineData("charges at most 20", "LessOrEqual", 20)]
    public void TryParseAmountThreshold_ParsesComparisons(string text, string comparator, decimal low)
    {
        var threshold = AiAssistantService.TryParseAmountThreshold(text);
        Assert.NotNull(threshold);
        Assert.Equal(comparator, threshold!.Comparator.ToString());
        Assert.Equal(low, threshold.Low);
    }

    [Fact]
    public void TryParseAmountThreshold_ParsesBetween()
    {
        var threshold = AiAssistantService.TryParseAmountThreshold("show purchases between 50 and 200");
        Assert.NotNull(threshold);
        Assert.Equal("Between", threshold!.Comparator.ToString());
        Assert.Equal(50, threshold.Low);
        Assert.Equal(200, threshold.High);
    }

    [Theory]
    [InlineData("how much did I spend in March 2026")] // a bare year is not a comparison
    [InlineData("show my recent transactions")]
    public void TryParseAmountThreshold_IgnoresNonComparisons(string text)
        => Assert.Null(AiAssistantService.TryParseAmountThreshold(text));

    [Fact]
    public void BuildThresholdMatches_FiltersOutflowsAndExcludesTransfers()
    {
        var rows = new List<AiAssistantService.AiTransactionRow>
        {
            Row("a", 2026, 7, 1, "TV", -300),
            Row("b", 2026, 7, 2, "Coffee", -8),
            Row("c", 2026, 7, 3, "Salary", 5000),                                   // inflow, ignored
            Row("d", 2026, 7, 4, "Move", -900, category: "Transfer", ledger: "Transfer:Growth") // transfer, ignored
        };
        var threshold = AiAssistantService.TryParseAmountThreshold("over 250")!;

        var json = System.Text.Json.JsonSerializer.Serialize(
            AiAssistantService.BuildThresholdMatches(rows, threshold, sampleWasComplete: true));

        Assert.Contains("\"count\":1", json);
        Assert.Contains("TV", json);
        Assert.DoesNotContain("Salary", json);
        Assert.DoesNotContain("Move", json);
    }

    // ---------- ledger-balance target forecast ----------

    [Fact]
    public void TryParseLedgerBalanceForecast_ParsesLedgerAndTarget()
    {
        var parsed = AiAssistantService.TryParseLedgerBalanceForecast("how long until my Growth reaches 50000");
        Assert.NotNull(parsed);
        Assert.Equal("Growth", parsed!.Value.Ledger);
        Assert.Equal(50000, parsed.Value.Target);
    }

    [Fact]
    public void TryParseLedgerBalanceForecast_RequiresForecastVerb()
        => Assert.Null(AiAssistantService.TryParseLedgerBalanceForecast("my Growth balance is 50000"));

    [Fact]
    public void ComputeLedgerBalanceForecast_ProjectsFromAveragePositiveSavings()
    {
        // Two prior cycles each add 1000 to Growth; current balance 2000, target 5000 -> 3 cycles.
        var cycles = new[]
        {
            new AiAssistantService.CycleKey(2026, 5),
            new AiAssistantService.CycleKey(2026, 6)
        };
        var txs = new List<AiAssistantService.AiTransactionRow>
        {
            Row("m", 2026, 5, 10, "Deposit", 1000, category: "Investment", ledger: "Growth"),
            Row("j", 2026, 6, 10, "Deposit", 1000, category: "Investment", ledger: "Growth")
        };

        var result = AiAssistantService.ComputeLedgerBalanceForecast(
            "Growth", target: 5000, currentBalance: 2000, txs, cycles, cycleDay: 1,
            today: new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("estimated-from-completed-cycles", result.Status);
        Assert.Equal(1000m, result.SavingsPerCycle);
        Assert.Equal(3000m, result.Remaining);
        Assert.Equal(3, result.EstimatedCycles);
        Assert.NotNull(result.EstimatedDate);
    }

    [Fact]
    public void ComputeLedgerBalanceForecast_AlreadyReached_WhenCurrentMeetsTarget()
    {
        var result = AiAssistantService.ComputeLedgerBalanceForecast(
            "Stability", target: 1000, currentBalance: 1200, [], [], cycleDay: 1,
            today: new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal("already-reached", result.Status);
        Assert.Equal(0m, result.Remaining);
    }

    [Fact]
    public void ComputeLedgerBalanceForecast_NotReachable_WhenNoPositiveSavings()
    {
        var cycles = new[] { new AiAssistantService.CycleKey(2026, 6) };
        var txs = new List<AiAssistantService.AiTransactionRow>
        {
            Row("j", 2026, 6, 10, "Spend", -50, ledger: "Growth")
        };
        var result = AiAssistantService.ComputeLedgerBalanceForecast(
            "Growth", target: 5000, currentBalance: 100, txs, cycles, cycleDay: 1,
            today: new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal("not-currently-reachable", result.Status);
    }

    // ---------- recurring cost summary ----------

    [Theory]
    [InlineData("how much do my subscriptions cost a month", true)]
    [InlineData("total recurring per year", true)]
    [InlineData("list my subscriptions", false)]
    public void WantsRecurringCostSummary_DetectsCostPhrasing(string text, bool expected)
        => Assert.Equal(expected, AiAssistantService.WantsRecurringCostSummary(text));

    [Fact]
    public void BuildRecurringCostSummary_NormalizesFrequenciesToMonthly()
    {
        var recurring = new List<AiAssistantService.AiRecurringRow>
        {
            new("1", "Netflix", 12m, "Entertainment", "Rewards", "2026-01-01", null, 1, true, "Monthly", "2026-08-01"),
            new("2", "Gym", 10m, "Hobbies", "Rewards", "2026-01-01", null, 1, true, "Weekly", "2026-07-15"),
            new("3", "Domain", 120m, "Software", "Essentials", "2026-01-01", null, 1, true, "Annually", "2027-01-01"),
            new("4", "OldPlan", 99m, "Software", "Essentials", "2026-01-01", null, 1, false, "Monthly", "2026-08-01") // inactive
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            AiAssistantService.BuildRecurringCostSummary(recurring));

        // 12 (monthly) + 10*52/12 (weekly ~43.33) + 120/12 (annually = 10) = ~65.33
        Assert.Contains("\"activeCount\":3", json);
        Assert.Contains("65.33", json);
        Assert.DoesNotContain("OldPlan", json);
    }
}
