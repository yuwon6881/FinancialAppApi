using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Accounts;
using FinancialAppApi.Services.AI.Tools;
using FinancialAppApi.Services.Forecast;
using Microsoft.Extensions.Logging.Abstractions;
using static FinancialAppApi.Tests.AiTools.AiToolTestSupport;

namespace FinancialAppApi.Tests.AiTools;

// Ask AI's forecast must be the forecast workspace's forecast: the tool hands the same scenario to
// CashFlowForecastService and reports its figures, never a second projection.
public sealed class ForecastCashFlowToolTests
{
    // Cycle day 28: today sits in the cycle that ends on 27 November.
    private static readonly DateOnly ForecastToday = new(2026, 11, 23);

    private static CashFlowForecastService NewService(AppDbContext context)
    {
        var clock = new FinancialClock(
            TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")),
            new FixedTimeProvider(new DateTimeOffset(ForecastToday.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero)));
        var accounts = new LedgerAccountService(
            context, new LedgerAccountBalanceService(context), new CycleBalanceService(context), clock);
        return new CashFlowForecastService(
            context, accounts, new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance), clock);
    }

    private static AiToolContext Context(bool sensitive = false) =>
        new(sensitive, 28, "MYR", ForecastToday, _ => Task.FromResult(sensitive));

    private static AppDbContext Seed()
    {
        var context = TestHelpers.NewInMemoryContext();
        TestHelpers.SeedLedgerAccounts(context);
        context.FinancialSettings.Add(new FinancialSetting
        {
            UserId = TestHelpers.DefaultUserId,
            CycleDay = 28,
            TargetStabilityFund = 10000m,
            EssentialsAlloc = 50m,
            GrowthAlloc = 20m,
            StabilityAlloc = 20m,
            RewardsAlloc = 10m,
        });
        context.Transactions.Add(new Transaction
        {
            Id = "opening",
            UserId = TestHelpers.DefaultUserId,
            Date = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            PostedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            Description = "Opening",
            Category = "Opening",
            LedgerCategory = "Essentials",
            Amount = 3000m,
            AccountId = TestHelpers.AccountIdFor("Essentials"),
        });
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "gym",
            UserId = TestHelpers.DefaultUserId,
            Name = "Gym",
            Amount = 170m,
            Frequency = "Monthly",
            Category = "Health",
            LedgerCategory = "Essentials",
            AccountId = TestHelpers.AccountIdFor("Essentials"),
            DueDate = 5,
            StartDate = "2026-01-05",
            Active = true,
        });
        context.SaveChanges();
        return context;
    }

    [Fact]
    public async Task DefaultCall_ReportsTheWorkspaceForecastForFourCycles()
    {
        await using var db = Seed();
        var service = NewService(db);
        var context = Context();
        var expected = await service.ForecastAsync(new CashFlowScenario(4, null, null, [], []), CancellationToken.None);

        var data = Parse(await RunAsync(NewExecutor(new ForecastCashFlowTool(service)), context, "forecast_cash_flow"))
            .GetProperty("data");

        var cycles = data.GetProperty("cycles").EnumerateArray().ToList();
        Assert.Equal(expected.Forecast.Cycles.Count, cycles.Count);
        Assert.Equal(5, cycles.Count);
        for (var index = 0; index < cycles.Count; index++)
        {
            foreach (var bucket in ForecastBuckets.All)
            {
                Assert.Equal(expected.Forecast.Cycles[index].Closing[bucket],
                    cycles[index].GetProperty("closing").GetProperty(bucket).GetDecimal());
            }
        }
        Assert.False(data.GetProperty("incomeKnown").GetBoolean());
        // The gym bill leaves Essentials on 5 December, and its id is evidence the model may cite.
        var gym = cycles[1].GetProperty("lines").EnumerateArray().Single();
        Assert.Equal("Gym", gym.GetProperty("label").GetString());
        Assert.Equal(-170m, gym.GetProperty("amount").GetDecimal());
        Assert.Equal("gym:2026-12-05", gym.GetProperty("billKey").GetString());
        Assert.Equal(2320m, cycles[^1].GetProperty("closing").GetProperty("Essentials").GetDecimal());
        Assert.True(context.Evidence.Contains(AiEvidenceLedger.Recurring, "gym"));
    }

    [Fact]
    public async Task Scenario_PassesIncomeWhatIfsAndSkippedBillsToTheSameService()
    {
        await using var db = Seed();
        var service = NewService(db);
        var scenario = new CashFlowScenario(
            2,
            1000m,
            200m,
            [new CashFlowScenarioItem(new DateOnly(2026, 12, 10), -500m, "Rewards", "Concert")],
            ["gym:2026-12-05"]);
        var expected = await service.ForecastAsync(scenario, CancellationToken.None);

        var data = Parse(await RunAsync(NewExecutor(new ForecastCashFlowTool(service)), Context(), "forecast_cash_flow",
            """
            {"cycles":2,"incomePerCycle":1000,"everydaySpendingPerCycle":200,
             "whatIfs":[{"date":"2026-12-10","amount":-500,"bucket":"Rewards","label":"Concert"}],
             "skipBills":["gym:2026-12-05"]}
            """)).GetProperty("data");

        var last = data.GetProperty("cycles").EnumerateArray().Last().GetProperty("closing");
        foreach (var bucket in ForecastBuckets.All)
            Assert.Equal(expected.Forecast.Cycles[^1].Closing[bucket], last.GetProperty(bucket).GetDecimal());
        Assert.True(data.GetProperty("incomeKnown").GetBoolean());
        Assert.True(data.GetProperty("everydaySpendingIncluded").GetBoolean());
        Assert.Equal("Gym", data.GetProperty("skippedBills")[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task OutOfRangeScenario_IsACorrectableArgumentError()
    {
        await using var db = Seed();
        var executor = NewExecutor(new ForecastCashFlowTool(NewService(db)));

        var tooLong = Parse(await RunAsync(executor, Context(), "forecast_cash_flow", """{"cycles":13}"""));
        var badBucket = Parse(await RunAsync(executor, Context(), "forecast_cash_flow",
            """{"whatIfs":[{"date":"2026-12-10","amount":-5,"bucket":"Holiday","label":"x"}]}"""));

        Assert.False(tooLong.GetProperty("ok").GetBoolean());
        Assert.Contains("cycles", tooLong.GetProperty("error").GetString());
        Assert.False(badBucket.GetProperty("ok").GetBoolean());
        Assert.Contains("Essentials", badBucket.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SensitiveMode_HidesTheForecastEntirely()
    {
        await using var db = Seed();

        var hidden = await RunAsync(NewExecutor(new ForecastCashFlowTool(NewService(db))), Context(sensitive: true), "forecast_cash_flow");

        var root = Parse(hidden);
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Contains("sensitive mode", root.GetProperty("error").GetString());
        Assert.DoesNotContain("3000", hidden.Output);
        Assert.False(root.TryGetProperty("data", out _));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
