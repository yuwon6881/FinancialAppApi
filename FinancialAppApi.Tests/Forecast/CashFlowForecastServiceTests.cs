using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Accounts;
using FinancialAppApi.Services.Forecast;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests.Forecast;

public sealed class CashFlowForecastServiceTests
{
    // Cycle day 28: today sits in the cycle that ends on 27 November.
    private static readonly DateOnly Today = new(2026, 11, 23);

    private static CashFlowForecastService NewService(AppDbContext context)
    {
        var clock = new FinancialClock(
            TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")),
            new FixedTimeProvider(new DateTimeOffset(Today.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero)));
        var accounts = new LedgerAccountService(
            context, new LedgerAccountBalanceService(context), new CycleBalanceService(context), clock);
        return new CashFlowForecastService(
            context, accounts, new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance), clock);
    }

    private static CashFlowScenario Scenario(
        int cycles = 2,
        decimal? income = null,
        IReadOnlyList<CashFlowScenarioItem>? items = null,
        IReadOnlyCollection<string>? skipped = null) =>
        new(cycles, income, null, items ?? [], skipped ?? []);

    private static AppDbContext Seed(bool active = true)
    {
        var context = TestHelpers.NewInMemoryContext();
        TestHelpers.SeedLedgerAccounts(context);
        context.FinancialSettings.Add(new FinancialSetting { UserId = TestHelpers.DefaultUserId, CycleDay = 28, TargetStabilityFund = 10000m });
        context.Transactions.Add(new Transaction
        {
            Id = "salary",
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
            Active = active,
        });
        context.SaveChanges();
        return context;
    }

    [Fact]
    public async Task Forecast_StartsFromRealBalances_AndTakesEachUpcomingBill()
    {
        await using var context = Seed();

        var response = await NewService(context).ForecastAsync(Scenario(), CancellationToken.None);

        var cycles = response.Forecast.Cycles;
        Assert.Equal(3, cycles.Count);
        Assert.True(cycles[0].IsCurrent);
        Assert.Equal(Today, cycles[0].Start);
        Assert.Equal(3000m, cycles[0].Opening["Essentials"]);
        // The gym bill on 5 Dec and 5 Jan each leave Essentials; none falls in the rest of November.
        Assert.Empty(cycles[0].Lines);
        var decemberBill = Assert.Single(cycles[1].Lines);
        Assert.Equal(new DateOnly(2026, 12, 5), decemberBill.Date);
        Assert.Equal(-170m, decemberBill.Amount);
        Assert.Equal(CashFlowForecastService.BillKey("gym", new DateOnly(2026, 12, 5)), decemberBill.SourceId);
        Assert.Equal(2660m, cycles[2].Closing["Essentials"]);
        Assert.False(response.Forecast.IncomeKnown);
    }

    [Fact]
    public async Task SkippedBills_AndPausedBills_AreLeftOut()
    {
        await using var context = Seed();
        var skipped = new[] { CashFlowForecastService.BillKey("gym", new DateOnly(2026, 12, 5)) };

        var response = await NewService(context).ForecastAsync(Scenario(skipped: skipped), CancellationToken.None);

        Assert.Empty(response.Forecast.Cycles[1].Lines);
        Assert.Single(response.Forecast.Cycles[2].Lines);
        var named = Assert.Single(response.SkippedBills);
        Assert.Equal("Gym", named.Label);
        Assert.Equal(skipped[0], named.SourceId);

        await using var paused = Seed(active: false);
        var pausedResponse = await NewService(paused).ForecastAsync(Scenario(), CancellationToken.None);
        Assert.All(pausedResponse.Forecast.Cycles, cycle => Assert.Empty(cycle.Lines));
    }

    [Fact]
    public async Task TrackedOccurrences_OweOnlyWhatIsLeft()
    {
        await using var context = Seed();
        context.RecurringPaymentOccurrences.AddRange(
            new RecurringPaymentOccurrence
            {
                Id = "occ-dec",
                UserId = TestHelpers.DefaultUserId,
                RecurringPaymentId = "gym",
                OccurrenceDate = new DateOnly(2026, 12, 5),
                Name = "Gym",
                Status = RecurringOccurrenceStatus.Paid,
            },
            // Overdue and unpaid in the current cycle: still owed, so it lands on today.
            new RecurringPaymentOccurrence
            {
                Id = "occ-nov",
                UserId = TestHelpers.DefaultUserId,
                RecurringPaymentId = "gym",
                OccurrenceDate = new DateOnly(2026, 11, 5),
                Name = "Gym",
                Status = RecurringOccurrenceStatus.Pending,
            });
        await context.SaveChangesAsync();

        var response = await NewService(context).ForecastAsync(Scenario(), CancellationToken.None);

        var overdue = Assert.Single(response.Forecast.Cycles[0].Lines);
        Assert.Equal(Today, overdue.Date);
        Assert.Empty(response.Forecast.Cycles[1].Lines);
    }

    [Fact]
    public async Task ScenarioItems_AreEvaluated_WithoutWritingAnything()
    {
        await using var context = Seed();
        var items = new[] { new CashFlowScenarioItem(new DateOnly(2026, 12, 10), -1800m, "essentials", " Tooth crown ") };
        var before = context.ChangeTracker.Entries().Count();

        var response = await NewService(context).ForecastAsync(Scenario(income: 4800m, items: items), CancellationToken.None);

        var crown = Assert.Single(response.Forecast.Cycles[1].Lines, line => line.Kind == ForecastItemKinds.Scenario && line.Amount < 0);
        Assert.Equal("Tooth crown", crown.Label);
        Assert.Equal("Essentials", crown.Bucket);
        Assert.Equal(0, context.Transactions.Count(transaction => transaction.Id != "salary"));
        Assert.Equal(before, context.ChangeTracker.Entries().Count());
    }

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(13, null, null)]
    [InlineData(4, -1, null)]
    [InlineData(4, null, "Savings")]
    public void Validate_RejectsOutOfRangeScenarios(int cycles, int? income, string? bucket)
    {
        var items = bucket is null ? [] : new[] { new CashFlowScenarioItem(Today, -10m, bucket, "Thing") };

        Assert.NotNull(CashFlowForecastService.Validate(new CashFlowScenario(cycles, income, null, items, [])));
    }

    [Fact]
    public void Validate_RejectsNegativeIncomeItems_AndAcceptsAPlainScenario()
    {
        var negativeIncome = new[] { new CashFlowScenarioItem(Today, -10m, null, "Refund") };

        Assert.NotNull(CashFlowForecastService.Validate(new CashFlowScenario(4, null, null, negativeIncome, [])));
        Assert.Null(CashFlowForecastService.Validate(new CashFlowScenario(4, 4800m, 600m, [], [])));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
