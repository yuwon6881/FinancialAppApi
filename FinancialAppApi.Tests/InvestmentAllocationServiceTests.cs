using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests;

public sealed class InvestmentAllocationServiceTests
{
    [Theory]
    [InlineData(66, 10, 24, 3, 5, true)]
    [InlineData(65, 10, 24, 3, 5, false)]
    [InlineData(0, 50, 50, 3, 5, false)]
    [InlineData(66, 10, 24, 5, 5, false)]
    [InlineData(66, 10, 24, 6, 5, false)]
    public void PlanValidation_EnforcesPositiveExactTargetsAndOrderedBands(
        decimal us,
        decimal international,
        decimal bonds,
        decimal watch,
        decimal alert,
        bool valid)
    {
        var error = InvestmentAllocationService.ValidatePlan(
            new InvestmentPlanMutationDto(us, international, bonds, watch, alert));

        Assert.Equal(valid, error is null);
    }

    [Fact]
    public async Task Allocation_AggregatesAccounts_ExcludesCashAndUsesCashBeforeContribution()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var first = new InvestmentAccount { Name = "Broker one", BaseCurrency = "USD" };
        var second = new InvestmentAccount { Name = "Broker two", BaseCurrency = "USD" };
        var us = Instrument("VTI", "USEquity");
        var international = Instrument("VXUS", "InternationalExUS");
        var bonds = Instrument("BND", "Bonds");
        context.InvestmentAccounts.AddRange(first, second);
        context.InvestmentInstruments.AddRange(us, international, bonds);
        await context.SaveChangesAsync();
        AddPosition(context, first, us, 40);
        AddPosition(context, second, us, 40);
        AddPosition(context, first, international, 10);
        AddPosition(context, second, bonds, 10);
        context.InvestmentCashFlows.Add(new InvestmentCashFlow
        {
            AccountId = first.Id,
            Currency = "USD",
            Type = "Deposit",
            // Fund this account's $50 of buys and leave $5 available for allocation.
            Amount = 55,
            Date = DateOnly.FromDateTime(DateTime.UtcNow)
        });
        await context.SaveChangesAsync();

        var portfolio = await NewPortfolioService(context).GetPortfolioAsync("1m", CancellationToken.None);

        Assert.Equal("Alert", portfolio.Allocation.Status);
        Assert.Equal(100, portfolio.Allocation.InvestedValue);
        Assert.Equal(5, portfolio.Allocation.AvailableCash);
        Assert.Equal(80, portfolio.Allocation.Sleeves.Single(value => value.Sleeve == "USEquity").Value);
        Assert.Equal(16.21m, portfolio.Allocation.MinimumContribution);
        Assert.Equal("UseCash", portfolio.Allocation.Recommendations[0].Kind);
        Assert.DoesNotContain(portfolio.Allocation.Recommendations, value => value.Kind == "TopUp");
        Assert.Contains(portfolio.Allocation.Recommendations, value => value.Kind == "Sell");
        Assert.Contains(portfolio.Allocation.Recommendations, value => value.Kind == "Buy");
        Assert.Contains(portfolio.Allocation.Recommendations, value => value.Kind == "TransferBuy");
    }

    [Fact]
    public async Task Allocation_IsIncompleteAndSuppressesAmountsForUnassignedHolding()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var instrument = Instrument("VTI", null);
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();
        AddPosition(context, account, instrument, 100);
        await context.SaveChangesAsync();

        var allocation = (await NewPortfolioService(context)
            .GetPortfolioAsync("1m", CancellationToken.None)).Allocation;

        Assert.Equal("Incomplete", allocation.Status);
        Assert.Empty(allocation.Recommendations);
        Assert.Null(allocation.MinimumContribution);
        Assert.Contains(allocation.IncompleteReasons, value => value.Contains("assigned", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Allocation_UsesUsualCycleContributionBeforeSuggestingSales()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var us = Instrument("VTI", "USEquity");
        var international = Instrument("VXUS", "InternationalExUS");
        var bonds = Instrument("BND", "Bonds");
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.AddRange(us, international, bonds);
        context.FinancialSettings.Add(new FinancialSetting { Currency = "USD", CycleDay = 1 });
        await context.SaveChangesAsync();
        AddPosition(context, account, us, 80);
        AddPosition(context, account, international, 10);
        AddPosition(context, account, bonds, 10);

        foreach (var monthsAgo in new[] { 1, 2 })
        {
            var date = DateTime.UtcNow.AddMonths(-monthsAgo);
            context.Transactions.Add(new Transaction
            {
                Id = $"growth-{monthsAgo}",
                Date = date,
                PostedAt = date,
                Description = "Income allocation",
                Category = "Income",
                LedgerCategory = "Growth",
                Amount = 100
            });
        }
        await context.SaveChangesAsync();

        var allocation = (await NewPortfolioService(context)
            .GetPortfolioAsync("1m", CancellationToken.None)).Allocation;

        Assert.Equal("TopUp", allocation.Recommendations[0].Kind);
        Assert.Equal(100, allocation.Recommendations[0].Amount);
        Assert.Contains("before considering any sale", allocation.Recommendations[0].Message);
        Assert.DoesNotContain(allocation.Recommendations, value => value.Kind == "Sell");
        Assert.Contains(allocation.Recommendations, value => value.Kind == "Buy");
        Assert.Equal(0, allocation.MinimumContribution);
    }

    [Fact]
    public async Task Allocation_OnTrack_HasNoGuidance()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var us = Instrument("VTI", "USEquity");
        var international = Instrument("VXUS", "InternationalExUS");
        var bonds = Instrument("BND", "Bonds");
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.AddRange(us, international, bonds);
        await context.SaveChangesAsync();
        AddPosition(context, account, us, 66);
        AddPosition(context, account, international, 10);
        AddPosition(context, account, bonds, 24);
        await context.SaveChangesAsync();

        var allocation = (await NewPortfolioService(context)
            .GetPortfolioAsync("1m", CancellationToken.None)).Allocation;

        Assert.Equal("OnTrack", allocation.Status);
        Assert.Empty(allocation.Recommendations);
    }

    [Fact]
    public async Task Allocation_OnlySuggestsSalesWhenUsualGrowthDepositCannotRestoreWatchBand()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "USD", CycleDay = 1 });
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var us = Instrument("VTI", "USEquity");
        var international = Instrument("VXUS", "InternationalExUS");
        var bonds = Instrument("BND", "Bonds");
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.AddRange(us, international, bonds);
        await context.SaveChangesAsync();
        AddPosition(context, account, us, 80);
        AddPosition(context, account, international, 10);
        AddPosition(context, account, bonds, 10);
        var completedCycle = DateTime.UtcNow.AddMonths(-1);
        context.Transactions.Add(new Transaction
        {
            Id = "small-growth",
            Date = completedCycle,
            PostedAt = completedCycle,
            Description = "Income allocation",
            Category = "Income",
            LedgerCategory = "Growth",
            Amount = 5
        });
        await context.SaveChangesAsync();

        var allocation = (await NewPortfolioService(context)
            .GetPortfolioAsync("1m", CancellationToken.None)).Allocation;

        Assert.Equal("TopUp", allocation.Recommendations[0].Kind);
        Assert.Equal("Buy", allocation.Recommendations[1].Kind);
        Assert.Contains(allocation.Recommendations, value => value.Kind == "Sell");
        Assert.Contains(allocation.Recommendations, value => value.Kind == "TransferBuy");
    }

    [Fact]
    public async Task ContributionPlan_SplitsARoutineDepositByTargetWhenOnTrack()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "USD", CycleDay = 1 });
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var us = Instrument("VTI", "USEquity");
        var international = Instrument("VXUS", "InternationalExUS");
        var bonds = Instrument("BND", "Bonds");
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.AddRange(us, international, bonds);
        context.InvestmentPlans.Add(new InvestmentPlan
        {
            UsEquityTarget = 34,
            InternationalExUsTarget = 33,
            BondsTarget = 33,
            WatchDrift = 3,
            AlertDrift = 5
        });
        await context.SaveChangesAsync();
        AddPosition(context, account, us, 340);
        AddPosition(context, account, international, 330);
        AddPosition(context, account, bonds, 330);
        AddGrowthDeposit(context, "growth-1", 1, 1000);
        await context.SaveChangesAsync();

        var allocation = (await NewPortfolioService(context)
            .GetPortfolioAsync("1m", CancellationToken.None)).Allocation;
        var plan = allocation.ContributionPlan;

        Assert.Equal("OnTrack", allocation.Status);
        // The routine split survives even though there is nothing to rebalance.
        Assert.NotNull(plan);
        Assert.Equal(1000, plan!.Amount);
        Assert.False(plan.IsEstimated);
        Assert.Equal(340, plan.Sleeves.Single(value => value.Sleeve == "USEquity").Amount);
        Assert.Equal(330, plan.Sleeves.Single(value => value.Sleeve == "InternationalExUS").Amount);
        Assert.Equal(330, plan.Sleeves.Single(value => value.Sleeve == "Bonds").Amount);
        Assert.Equal(1000, plan.Sleeves.Sum(value => value.Amount));
        Assert.All(plan.Sleeves, value => Assert.Equal(0, value.ProjectedDriftPercentagePoints));
    }

    [Fact]
    public async Task ContributionPlan_LeansTheDepositTowardTheUnderweightSleeve()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "USD", CycleDay = 1 });
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var us = Instrument("VTI", "USEquity");
        var international = Instrument("VXUS", "InternationalExUS");
        var bonds = Instrument("BND", "Bonds");
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.AddRange(us, international, bonds);
        context.InvestmentPlans.Add(new InvestmentPlan
        {
            UsEquityTarget = 34,
            InternationalExUsTarget = 33,
            BondsTarget = 33,
            WatchDrift = 3,
            AlertDrift = 5
        });
        await context.SaveChangesAsync();
        // Bonds are heavily underweight, so the same deposit should favour them.
        AddPosition(context, account, us, 500);
        AddPosition(context, account, international, 400);
        AddPosition(context, account, bonds, 100);
        AddGrowthDeposit(context, "growth-1", 1, 1000);
        await context.SaveChangesAsync();

        var plan = (await NewPortfolioService(context)
            .GetPortfolioAsync("1m", CancellationToken.None)).Allocation.ContributionPlan;

        Assert.NotNull(plan);
        var bondsAmount = plan!.Sleeves.Single(value => value.Sleeve == "Bonds").Amount;
        var usAmount = plan.Sleeves.Single(value => value.Sleeve == "USEquity").Amount;
        Assert.True(bondsAmount > usAmount, $"Expected bonds ({bondsAmount}) to receive more than US equity ({usAmount}).");
        Assert.Equal(1000, plan.Sleeves.Sum(value => value.Amount));
        // Cash-flow rebalancing alone cannot fully close a gap this large, but it must not
        // overshoot any sleeve past its target either.
        Assert.All(plan.Sleeves, value => Assert.True(value.ProjectedDriftPercentagePoints <= 0.01m));
    }

    [Fact]
    public async Task ContributionPlan_UsesMedianDepositAndFallsBackToIdleCash()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "USD", CycleDay = 1 });
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var us = Instrument("VTI", "USEquity");
        var international = Instrument("VXUS", "InternationalExUS");
        var bonds = Instrument("BND", "Bonds");
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.AddRange(us, international, bonds);
        await context.SaveChangesAsync();
        AddPosition(context, account, us, 66);
        AddPosition(context, account, international, 10);
        AddPosition(context, account, bonds, 24);
        // One windfall cycle must not drag the routine figure up: 100, 100, 900 -> 100.
        AddGrowthDeposit(context, "growth-1", 1, 100);
        AddGrowthDeposit(context, "growth-2", 2, 100);
        AddGrowthDeposit(context, "growth-3", 3, 900);
        await context.SaveChangesAsync();

        var plan = (await NewPortfolioService(context)
            .GetPortfolioAsync("1m", CancellationToken.None)).Allocation.ContributionPlan;

        Assert.NotNull(plan);
        Assert.Equal(100, plan!.Amount);
        Assert.Equal(3, plan.CyclesObserved);
        Assert.Contains("median", plan.Basis, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContributionPlan_FallsBackToUninvestedCashWhenThereIsNoDepositHistory()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "USD", CycleDay = 1 });
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var us = Instrument("VTI", "USEquity");
        var international = Instrument("VXUS", "InternationalExUS");
        var bonds = Instrument("BND", "Bonds");
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.AddRange(us, international, bonds);
        await context.SaveChangesAsync();
        AddPosition(context, account, us, 66);
        AddPosition(context, account, international, 10);
        AddPosition(context, account, bonds, 24);
        context.InvestmentCashFlows.Add(new InvestmentCashFlow
        {
            AccountId = account.Id,
            Currency = "USD",
            Type = "Deposit",
            Amount = 150,
            Date = DateOnly.FromDateTime(DateTime.UtcNow)
        });
        await context.SaveChangesAsync();

        var plan = (await NewPortfolioService(context)
            .GetPortfolioAsync("1m", CancellationToken.None)).Allocation.ContributionPlan;

        Assert.NotNull(plan);
        Assert.Equal(50, plan!.Amount);
        Assert.True(plan.IsEstimated);
        Assert.Equal(0, plan.CyclesObserved);
        Assert.Equal(50, plan.Sleeves.Sum(value => value.Amount));
    }

    [Fact]
    public void MutualFund_IsAFirstClassInstrumentType()
        => Assert.Contains("MutualFund", InvestmentKinds.InstrumentTypes);

    [Theory]
    [InlineData(2.99, "OnTrack")]
    [InlineData(3, "Watch")]
    [InlineData(4.99, "Watch")]
    [InlineData(5, "Alert")]
    public void DriftStatus_UsesInclusiveWatchAndAlertBoundaries(double drift, string expected)
        => Assert.Equal(expected, InvestmentAllocationService.ClassifyDrift((decimal)drift, 3, 5));

    private static InvestmentInstrument Instrument(string symbol, string? sleeve) => new()
    {
        Symbol = symbol,
        Name = symbol,
        Type = symbol == "BND" ? "MutualFund" : "ETF",
        Currency = "USD",
        IsCustom = true,
        AllocationSleeve = sleeve
    };

    private static void AddPosition(
        Database.AppDbContext context,
        InvestmentAccount account,
        InvestmentInstrument instrument,
        decimal value)
    {
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id,
            InstrumentId = instrument.Id,
            Instrument = instrument,
            Type = "Buy",
            TradeDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)),
            Units = value,
            UnitPrice = 1,
            CashAmount = value
        });
        context.ManualPriceOverrides.Add(new ManualPriceOverride
        {
            InstrumentId = instrument.Id,
            MarketDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Price = 1
        });
    }

    private static void AddGrowthDeposit(
        Database.AppDbContext context,
        string id,
        int monthsAgo,
        decimal amount)
    {
        var date = DateTime.UtcNow.AddMonths(-monthsAgo);
        context.Transactions.Add(new Transaction
        {
            Id = id,
            Date = date,
            PostedAt = date,
            Description = "Income allocation",
            Category = "Income",
            LedgerCategory = "Growth",
            Amount = amount
        });
    }

    private static InvestmentPortfolioService NewPortfolioService(Database.AppDbContext context)
        => new(context, new InvestmentAccountingService(), new StubProvider());

    private sealed class StubProvider : IMarketDataProvider
    {
        public bool IsConfigured => false;
        public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InstrumentSearchResult>>([]);
        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(string symbol, string? mic, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderPriceBar>>([]);
        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(string baseCurrency, string quoteCurrency, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderFxBar>>([]);
    }
}
