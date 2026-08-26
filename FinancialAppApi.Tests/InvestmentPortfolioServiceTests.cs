using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;
using System.Text.Json;

namespace FinancialAppApi.Tests;

public sealed class InvestmentPortfolioServiceTests
{
    [Fact]
    public async Task GetPortfolioAsync_IncludesPlanFxForClassifiedInstrumentWithoutHoldings()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "MYR" });
        context.InvestmentInstruments.Add(new InvestmentInstrument
        {
            Symbol = "BND", Name = "US bonds", Type = "ETF", Currency = "USD",
            AllocationSleeve = "Bonds", IsArchived = false
        });
        context.FxRateBars.Add(new FxRateBar
        {
            Provider = "test", BaseCurrency = "USD", QuoteCurrency = "MYR",
            MarketDate = new DateOnly(2026, 8, 25), Rate = 4.48m
        });
        await context.SaveChangesAsync();

        var portfolio = await NewService(context).GetPortfolioAsync("all", CancellationToken.None);

        var fx = Assert.Single(portfolio.PlanFxRates, rate => rate.Currency == "USD");
        Assert.Equal("USD", fx.Currency);
        Assert.Equal(4.48m, fx.RateToAppCurrency);
        Assert.Equal(new DateOnly(2026, 8, 25), fx.AsOf);
        Assert.Equal("Test data direct", fx.Source);
    }

    [Fact]
    public async Task GetAllocationAsync_MatchesTheFullPortfolioAllocation()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "MYR" });
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "MYR" };
        var instrument = new InvestmentInstrument
        {
            Symbol = "FUND", Name = "Fund", Type = "ETF", Currency = "MYR", IsCustom = true,
            AllocationSleeve = "USEquity"
        };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();
        context.InvestmentCashFlows.Add(new InvestmentCashFlow
        {
            AccountId = account.Id, Currency = "MYR", Type = "Deposit",
            Amount = 100m, Date = new DateOnly(2026, 7, 1)
        });
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
            Type = "Buy", TradeDate = new DateOnly(2026, 7, 2), Units = 5m,
            UnitPrice = 10m, CashAmount = 50m
        });
        await context.SaveChangesAsync();

        var service = NewService(context);
        var portfolio = await service.GetPortfolioAsync("1m", CancellationToken.None);
        var allocation = await service.GetAllocationAsync(CancellationToken.None);

        Assert.Equal(JsonSerializer.Serialize(portfolio.Allocation), JsonSerializer.Serialize(allocation));
    }

    [Fact]
    public async Task EndOfDayClose_ConvertsHoldingAndAddsSettlementCashWithoutEarlyRounding()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "MYR" });
        var account = new InvestmentAccount { Name = "Moomoo", BaseCurrency = "MYR" };
        var instrument = new InvestmentInstrument
        {
            Symbol = "VOO", Name = "Vanguard S&P 500 ETF", Type = "ETF", Currency = "USD",
            ProviderSymbol = "VOO", ProviderMic = "ARCX"
        };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
            Type = "Buy", TradeDate = new DateOnly(2026, 7, 1),
            Units = 0.7642m, UnitPrice = 650m, CashAmount = 496.73m
        });
        context.InvestmentCashFlows.Add(new InvestmentCashFlow
        {
            AccountId = account.Id, Currency = "MYR", Type = "Deposit",
            Amount = 2.13m, Date = new DateOnly(2026, 7, 23)
        });
        context.InvestmentCashFlows.Add(new InvestmentCashFlow
        {
            AccountId = account.Id, Currency = "USD", Type = "Deposit",
            Amount = 496.73m, Date = new DateOnly(2026, 7, 1)
        });
        context.MarketPriceBars.Add(new MarketPriceBar
        {
            Provider = "test", ExternalInstrumentId = "VOO|ARCX",
            Symbol = "VOO", Mic = "ARCX", MarketDate = new DateOnly(2026, 7, 23), Close = 678.61m
        });
        context.FxRateBars.AddRange(
            new FxRateBar
            {
                Provider = "test",
                BaseCurrency = "USD", QuoteCurrency = "MYR", MarketDate = new DateOnly(2026, 7, 1),
                Rate = 4.1m
            },
            new FxRateBar
            {
                Provider = "test",
                BaseCurrency = "USD", QuoteCurrency = "MYR", MarketDate = new DateOnly(2026, 7, 23),
                Rate = 4.0968097876965978622781814333m
            });
        await context.SaveChangesAsync();

        var portfolio = await NewService(context).GetPortfolioAsync("all", CancellationToken.None);

        var holding = Assert.Single(portfolio.Holdings);
        Assert.Equal(678.61m, holding.LatestPriceNative);
        Assert.Equal(4.0968097876965978622781814333m, holding.FxRate);
        Assert.Equal("Test data direct", holding.FxSource);
        Assert.Equal(2124.58m, decimal.Round(holding.ValueApp!.Value, 2));
        Assert.Equal(2126.71m, decimal.Round(portfolio.Summary.TotalValue!.Value, 2));
    }

    [Fact]
    public async Task PriceBars_DoNotAffectFx_ProviderRateIsUsed()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "MYR" });
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument
        {
            Symbol = "TEST", Name = "Test", Type = "Stock", Currency = "USD", ProviderSymbol = "TEST"
        };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
            Type = "Buy", TradeDate = new DateOnly(2026, 7, 1),
            Units = 1, UnitPrice = 10, CashAmount = 10
        });
        context.MarketPriceBars.AddRange(
            new MarketPriceBar { Provider = "test", ExternalInstrumentId = "TEST|", Symbol = "TEST", MarketDate = new DateOnly(2026, 7, 20), Close = 10 },
            new MarketPriceBar { Provider = "test", ExternalInstrumentId = "TEST|", Symbol = "TEST", MarketDate = new DateOnly(2026, 7, 23), Close = 10 });
        context.FxRateBars.Add(new FxRateBar
        {
            Provider = "test",
            BaseCurrency = "USD", QuoteCurrency = "MYR", MarketDate = new DateOnly(2026, 7, 22), Rate = 4.2m
        });
        await context.SaveChangesAsync();

        var holding = Assert.Single((await NewService(context).GetPortfolioAsync("all", CancellationToken.None)).Holdings);

        Assert.Equal(4.2m, holding.FxRate);
        Assert.Equal("Test data direct", holding.FxSource);
    }

    [Fact]
    public async Task ChangeToday_IncludesFxMovementBetweenTheTwoPriceDates()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "MYR" });
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument
        {
            Symbol = "TEST", Name = "Test", Type = "Stock", Currency = "USD", ProviderSymbol = "TEST"
        };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
            Type = "Buy", TradeDate = new DateOnly(2026, 7, 1), Units = 2, UnitPrice = 10, CashAmount = 20
        });
        context.MarketPriceBars.AddRange(
            new MarketPriceBar { Provider = "test", ExternalInstrumentId = "TEST|", Symbol = "TEST", MarketDate = new DateOnly(2026, 7, 20), Close = 10 },
            new MarketPriceBar { Provider = "test", ExternalInstrumentId = "TEST|", Symbol = "TEST", MarketDate = new DateOnly(2026, 7, 23), Close = 12 });
        context.FxRateBars.AddRange(
            new FxRateBar { Provider = "test", BaseCurrency = "USD", QuoteCurrency = "MYR", MarketDate = new DateOnly(2026, 7, 20), Rate = 4 },
            new FxRateBar { Provider = "test", BaseCurrency = "USD", QuoteCurrency = "MYR", MarketDate = new DateOnly(2026, 7, 23), Rate = 5 });
        await context.SaveChangesAsync();

        var portfolio = await NewService(context).GetPortfolioAsync("all", CancellationToken.None);
        var holding = Assert.Single(portfolio.Holdings);

        // Two units moved from MYR 40 to MYR 60 each: (12*5 - 10*4) * 2.
        Assert.Equal(40m, holding.DailyChangeApp);
        Assert.Equal(40m, portfolio.Summary.DailyChange);
        Assert.Equal(120m, portfolio.Summary.MarketValue);
        Assert.Null(portfolio.Summary.CostBasis);
    }

    [Fact]
    public async Task ChangeToday_IsUnavailableWithoutAPreviousPrice()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument
        {
            Symbol = "TEST", Name = "Test", Type = "Stock", Currency = "USD", ProviderSymbol = "TEST"
        };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
            Type = "Buy", TradeDate = new DateOnly(2026, 7, 1), Units = 1, UnitPrice = 10, CashAmount = 10
        });
        context.MarketPriceBars.Add(new MarketPriceBar
        {
            Provider = "test", ExternalInstrumentId = "TEST|", Symbol = "TEST",
            MarketDate = new DateOnly(2026, 7, 23), Close = 12
        });
        await context.SaveChangesAsync();

        var portfolio = await NewService(context).GetPortfolioAsync("all", CancellationToken.None);

        Assert.Null(Assert.Single(portfolio.Holdings).DailyChangeApp);
        Assert.Null(portfolio.Summary.DailyChange);
    }

    [Fact]
    public async Task YearlyReturn_UsesActualExternalFlowDatesAndDoesNotDependOnChartRange()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument { Symbol = "CASH", Name = "Cash income", Type = "Other", Currency = "USD", IsCustom = true };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();
        context.InvestmentCashFlows.Add(new InvestmentCashFlow
        {
            AccountId = account.Id, Currency = "USD", Type = "Deposit",
            Amount = 1000, Date = today.AddDays(-365)
        });
        context.InvestmentTransactions.AddRange(
            new InvestmentTransaction
            {
                AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
                Type = "Buy", TradeDate = today.AddDays(-3), Units = 1, UnitPrice = 1, CashAmount = 1
            },
            new InvestmentTransaction
            {
                AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
                Type = "Dividend", TradeDate = today.AddDays(-2), CashAmount = 100
            },
            new InvestmentTransaction
            {
                AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
                Type = "Sell", TradeDate = today.AddDays(-1), Units = 1, UnitPrice = 1, CashAmount = 1
            });
        await context.SaveChangesAsync();

        var shortRange = await NewService(context).GetPortfolioAsync("1m", CancellationToken.None);
        var allRange = await NewService(context).GetPortfolioAsync("all", CancellationToken.None);

        Assert.NotNull(shortRange.Summary.AnnualReturn);
        Assert.InRange(shortRange.Summary.AnnualReturn!.Value, 0.09999m, 0.10001m);
        Assert.Equal(shortRange.Summary.AnnualReturn, allRange.Summary.AnnualReturn);
    }

    [Fact]
    public async Task Cash_CombinesDepositsWithdrawalsAndDividends_AndCountsTowardTotalValue()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument { Symbol = "AAPL", Name = "Apple", Type = "Stock", Currency = "USD", IsCustom = true };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();

        context.InvestmentCashFlows.AddRange(
            new InvestmentCashFlow { AccountId = account.Id, Currency = "USD", Type = "Deposit", Amount = 100, Date = new DateOnly(2025, 1, 1) },
            new InvestmentCashFlow { AccountId = account.Id, Currency = "USD", Type = "Withdrawal", Amount = -30, Date = new DateOnly(2025, 2, 1) });
        // The round trip leaves no holding or cash effect, while giving the dividend a valid
        // held-units history for the accounting lifecycle invariant.
        context.InvestmentTransactions.AddRange(
            new InvestmentTransaction
            {
                AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
                Type = "Buy", TradeDate = new DateOnly(2025, 2, 15), Units = 1, UnitPrice = 1, CashAmount = 1
            },
            new InvestmentTransaction
            {
                AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
                Type = "Dividend", TradeDate = new DateOnly(2025, 3, 1), CashAmount = 50, Taxes = 5
            },
            new InvestmentTransaction
            {
                AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
                Type = "Sell", TradeDate = new DateOnly(2025, 3, 15), Units = 1, UnitPrice = 1, CashAmount = 1
            });
        await context.SaveChangesAsync();

        var portfolio = await NewService(context).GetPortfolioAsync("all", CancellationToken.None);

        var balance = Assert.Single(portfolio.CashBalances);
        // 100 deposit - 30 withdrawal + (50 dividend - 5 tax) = 115
        Assert.Equal(115m, balance.Amount);
        Assert.Equal(115m, balance.AmountApp);
        Assert.Equal(115m, portfolio.Summary.CashValue);
        // No priced holdings, so market value is 0 and total value is just the cash.
        Assert.Equal(115m, portfolio.Summary.TotalValue);
    }

    [Fact]
    public async Task Buy_ReducesDepositedCash()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument { Symbol = "AAPL", Name = "Apple", Type = "Stock", Currency = "USD", IsCustom = true };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();

        context.InvestmentCashFlows.Add(new InvestmentCashFlow { AccountId = account.Id, Currency = "USD", Type = "Deposit", Amount = 1000, Date = new DateOnly(2025, 1, 1) });
        context.InvestmentTransactions.Add(
            new InvestmentTransaction { AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument, Type = "Buy", TradeDate = new DateOnly(2025, 2, 1), Units = 2, UnitPrice = 10, CashAmount = 20, Fees = 1 });
        await context.SaveChangesAsync();

        var portfolio = await NewService(context).GetPortfolioAsync("all", CancellationToken.None);

        var balance = Assert.Single(portfolio.CashBalances);
        // deposit 1000; buy costs 20 + 1 fee => 979
        Assert.Equal(979m, balance.Amount);
    }

    [Fact]
    public async Task FundingSummary_SeparatesGrowthContributions_CurrentGrowthBalance_AndBrokerNetDeposits()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "MYR" });
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        context.InvestmentAccounts.Add(account);
        await context.SaveChangesAsync();

        var date = new DateOnly(2026, 7, 1);
        context.Transactions.AddRange(
            new Transaction
            {
                Id = "growth-income",
                Date = date.ToDateTime(TimeOnly.MinValue),
                Description = "Income allocation",
                Category = "Income",
                LedgerCategory = "Growth",
                Amount = 1000
            },
            new Transaction
            {
                Id = "growth-spend",
                Date = date.AddDays(1).ToDateTime(TimeOnly.MinValue),
                Description = "Move to Rewards",
                Category = "Transfer",
                LedgerCategory = "Transfer:Growth->Rewards",
                Amount = 100
            });
        context.InvestmentCashFlows.AddRange(
            new InvestmentCashFlow
            {
                AccountId = account.Id, Currency = "USD", Type = "Deposit",
                Amount = 100, Date = date
            },
            new InvestmentCashFlow
            {
                AccountId = account.Id, Currency = "USD", Type = "Withdrawal",
                Amount = -20, Date = date.AddDays(1)
            });
        context.FxRateBars.Add(new FxRateBar
        {
            Provider = "test",
            BaseCurrency = "USD",
            QuoteCurrency = "MYR",
            MarketDate = date,
            Rate = 4
        });
        await context.SaveChangesAsync();

        var portfolio = await NewService(context)
            .GetPortfolioAsync("all", CancellationToken.None);
        var summary = portfolio.Summary;

        Assert.Equal(900m, summary.GrowthLedgerBalance);
        Assert.Equal(1000m, summary.GrowthContributions);
        Assert.Equal(320m, summary.NetDeposits);
        Assert.Equal(4m, portfolio.ReferenceRate);
        Assert.Equal(CurrencyCatalog.ReferenceCurrency, portfolio.ReferenceCurrency);
    }

    // The ledger read behind the funding summary is filtered in SQL rather than materialising every
    // transaction ever recorded. A salary reaches Growth as a percentage of an `IncomeSplit:` row,
    // not as a row labelled Growth, so a filter that only matched the literal label would drop every
    // salary and silently understate contributions — the balance would still add up, which is what
    // makes it worth pinning.
    [Fact]
    public async Task FundingSummary_CountsTheGrowthShareOfASalarySplit_AndIgnoresUnrelatedSpending()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "MYR" });
        var date = new DateOnly(2026, 7, 1);
        context.Transactions.AddRange(
            new Transaction
            {
                Id = "salary",
                Date = date.ToDateTime(TimeOnly.MinValue),
                Description = "Salary",
                Category = "Salary",
                LedgerCategory = "IncomeSplit:50,20,10,20",
                Amount = 1000
            },
            new Transaction
            {
                Id = "coffee",
                Date = date.AddDays(1).ToDateTime(TimeOnly.MinValue),
                Description = "Coffee",
                Category = "Food",
                LedgerCategory = "Essentials",
                Amount = -12
            });
        await context.SaveChangesAsync();

        var summary = (await NewService(context).GetPortfolioAsync("all", CancellationToken.None)).Summary;

        Assert.Equal(200m, summary.GrowthContributions);
        Assert.Equal(200m, summary.GrowthLedgerBalance);
    }

    [Fact]
    public async Task Holding_ExposesRealisedProfitLossAndDividends_WithoutChangingSummaryTotals()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "USD" });
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument
        {
            Symbol = "VTI", Name = "Vanguard Total Stock Market ETF", Type = "ETF", Currency = "USD", ProviderSymbol = "VTI"
        };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();

        context.InvestmentTransactions.AddRange(
            new InvestmentTransaction
            {
                AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
                Type = "Buy", TradeDate = new DateOnly(2026, 1, 2), Units = 10, UnitPrice = 10, CashAmount = 100, Fees = 2
            },
            new InvestmentTransaction
            {
                AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
                Type = "Sell", TradeDate = new DateOnly(2026, 2, 2), Units = 4, UnitPrice = 15, CashAmount = 60, Fees = 1
            },
            new InvestmentTransaction
            {
                AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
                Type = "Dividend", TradeDate = new DateOnly(2026, 3, 2), CashAmount = 12, Taxes = 2
            },
            new InvestmentTransaction
            {
                AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
                Type = "FeeTax", TradeDate = new DateOnly(2026, 4, 2), CashAmount = 3
            });
        context.MarketPriceBars.Add(new MarketPriceBar
        {
            Provider = "test", ExternalInstrumentId = "VTI|",
            Symbol = "VTI", MarketDate = new DateOnly(2026, 4, 2), Close = 12
        });
        await context.SaveChangesAsync();

        var portfolio = await NewService(context).GetPortfolioAsync("all", CancellationToken.None);
        var holding = Assert.Single(portfolio.Holdings);

        Assert.Equal(15.2m, holding.RealisedProfitLossApp);
        Assert.Equal(10m, holding.NetDividendsApp);
        Assert.Equal(holding.RealisedProfitLossApp, portfolio.Summary.RealisedProfitLoss);
        Assert.Equal(holding.NetDividendsApp, portfolio.Summary.NetDividends);
    }

    [Fact]
    public async Task ShadowProviderRowsCannotAffectTheActivePortfolio()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument
        {
            Symbol = "VOO", Name = "Fund", Type = "ETF", Currency = "USD", ProviderSymbol = "VOO"
        };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
            Type = "Buy", TradeDate = new DateOnly(2026, 1, 1), Units = 1, UnitPrice = 10, CashAmount = 10
        });
        context.MarketPriceBars.AddRange(
            new MarketPriceBar { Provider = "test", ExternalInstrumentId = "VOO|", Symbol = "VOO", MarketDate = new DateOnly(2026, 1, 2), Close = 12 },
            new MarketPriceBar { Provider = "shadow", ExternalInstrumentId = "VOO|", Symbol = "VOO", MarketDate = new DateOnly(2026, 1, 2), Close = 999 });
        await context.SaveChangesAsync();

        var holding = Assert.Single((await NewService(context).GetPortfolioAsync("all", CancellationToken.None)).Holdings);

        Assert.Equal(12m, holding.LatestPriceNative);
        Assert.Equal("Test data daily close", holding.PriceSource);
    }

    [Fact]
    public async Task GetPortfolioAsync_UsesStableAccountAndInvestmentOrdering()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "MYR" });
        var accountA = new InvestmentAccount { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Name = "Broker", BaseCurrency = "MYR" };
        var accountB = new InvestmentAccount { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Name = "Broker", BaseCurrency = "USD" };
        var archived = new InvestmentAccount { Id = Guid.Parse("00000000-0000-0000-0000-000000000003"), Name = "Old broker", BaseCurrency = "MYR", IsArchived = true };
        var instrumentA = new InvestmentInstrument { Id = Guid.Parse("10000000-0000-0000-0000-000000000001"), Symbol = "VWRA", Name = "Fund A", Type = "ETF", Currency = "USD" };
        var instrumentB = new InvestmentInstrument { Id = Guid.Parse("10000000-0000-0000-0000-000000000002"), Symbol = "VWRA", Name = "Fund B", Type = "ETF", Currency = "USD" };
        var instrumentC = new InvestmentInstrument { Id = Guid.Parse("10000000-0000-0000-0000-000000000003"), Symbol = "ZPRV", Name = "Fund C", Type = "ETF", Currency = "USD" };
        context.InvestmentAccounts.AddRange(accountB, archived, accountA);
        context.InvestmentInstruments.AddRange(instrumentB, instrumentC, instrumentA);
        await context.SaveChangesAsync();

        var portfolio = await NewService(context).GetPortfolioAsync("all", CancellationToken.None);

        Assert.Equal([accountA.Id, accountB.Id, archived.Id], portfolio.Accounts.Select(account => account.Id));
        Assert.Equal([instrumentA.Id, instrumentB.Id, instrumentC.Id], portfolio.Instruments.Select(instrument => instrument.Id));
    }

    private static InvestmentPortfolioService NewService(Database.AppDbContext context)
        => new(context, new InvestmentAccountingService(), new StubProvider());

    private sealed class StubProvider : IMarketDataProvider
    {
        public MarketDataProviderDescriptor Descriptor => new(
            "test", "Test data", false, MarketDataCapabilities.RequiredForActivation,
            new MarketDataQuotaPolicy(6, 750, 200));

        public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InstrumentSearchResult>>([]);

        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(MarketInstrumentReference instrument, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderPriceBar>>([]);

        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(string baseCurrency, string quoteCurrency, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderFxBar>>([]);

        public MarketInstrumentReference? TryResolveLegacyReference(string? symbol, string? mic)
            => string.IsNullOrWhiteSpace(symbol) ? null : new("test", $"{symbol}|{mic}");
    }
}
