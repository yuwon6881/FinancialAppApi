using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

// Pure projections the forecast and recurring tools quote: ledger time-to-target and
// frequency-normalized recurring cost.
public class AiDerivedInsightsTests
{
    private static AiAssistantService.AiTransactionRow Row(
        string id, int year, int month, int day, string description, decimal amount,
        string category = "Food", string ledger = "Essentials")
        => new(id, new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Utc),
            $"{year:D4}-{month:D2}-{day:D2}", description, category, ledger, amount);


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
