using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests;

public sealed class InvestmentPortfolioServiceTests
{
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
            Symbol = "VOO", Mic = "ARCX", MarketDate = new DateOnly(2026, 7, 23), Close = 678.61m
        });
        context.FxRateBars.AddRange(
            new FxRateBar
            {
                BaseCurrency = "USD", QuoteCurrency = "MYR", MarketDate = new DateOnly(2026, 7, 1),
                Rate = 4.1m
            },
            new FxRateBar
            {
                BaseCurrency = "USD", QuoteCurrency = "MYR", MarketDate = new DateOnly(2026, 7, 23),
                Rate = 4.0968097876965978622781814333m
            });
        await context.SaveChangesAsync();

        var portfolio = await NewService(context).GetPortfolioAsync("all", CancellationToken.None);

        var holding = Assert.Single(portfolio.Holdings);
        Assert.Equal(678.61m, holding.LatestPriceNative);
        Assert.Equal(4.0968097876965978622781814333m, holding.FxRate);
        Assert.Equal("Twelve Data direct", holding.FxSource);
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
            new MarketPriceBar { Symbol = "TEST", MarketDate = new DateOnly(2026, 7, 20), Close = 10 },
            new MarketPriceBar { Symbol = "TEST", MarketDate = new DateOnly(2026, 7, 23), Close = 10 });
        context.FxRateBars.Add(new FxRateBar
        {
            BaseCurrency = "USD", QuoteCurrency = "MYR", MarketDate = new DateOnly(2026, 7, 22), Rate = 4.2m
        });
        await context.SaveChangesAsync();

        var holding = Assert.Single((await NewService(context).GetPortfolioAsync("all", CancellationToken.None)).Holdings);

        Assert.Equal(4.2m, holding.FxRate);
        Assert.Equal("Twelve Data direct", holding.FxSource);
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
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id,
            InstrumentId = instrument.Id,
            Instrument = instrument,
            Type = "Dividend",
            TradeDate = new DateOnly(2025, 3, 1),
            CashAmount = 50,
            Taxes = 5
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
        Assert.Equal(4m, portfolio.UsdRate);
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
        public bool IsConfigured => false;

        public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InstrumentSearchResult>>([]);

        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(string symbol, string? mic, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderPriceBar>>([]);

        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(string baseCurrency, string quoteCurrency, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderFxBar>>([]);
    }
}
