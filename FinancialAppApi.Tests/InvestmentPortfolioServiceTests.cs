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
            MarketDate = DaysAgo(1), Rate = 4.48m
        });
        await context.SaveChangesAsync();

        var portfolio = await NewService(context).GetPortfolioAsync("all", CancellationToken.None);

        var fx = Assert.Single(portfolio.PlanFxRates, rate => rate.Currency == "USD");
        Assert.Equal("USD", fx.Currency);
        Assert.Equal(4.48m, fx.RateToAppCurrency);
        Assert.Equal(DaysAgo(1), fx.AsOf);
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
                BaseCurrency = "USD", QuoteCurrency = "MYR", MarketDate = DaysAgo(30),
                Rate = 4.1m
            },
            new FxRateBar
            {
                Provider = "test",
                BaseCurrency = "USD", QuoteCurrency = "MYR", MarketDate = DaysAgo(1),
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
            BaseCurrency = "USD", QuoteCurrency = "MYR", MarketDate = DaysAgo(1), Rate = 4.2m
        });
        await context.SaveChangesAsync();

        var holding = Assert.Single((await NewService(context).GetPortfolioAsync("all", CancellationToken.None)).Holdings);

        Assert.Equal(4.2m, holding.FxRate);
        Assert.Equal("Test data direct", holding.FxSource);
    }

    [Fact]
    public async Task ChangeToday_IsThePriceMoveAtTodaysRate()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "MYR" });
        var (account, instrument) = await SeedUsdFund(context);
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
            Type = "Buy", TradeDate = new DateOnly(2026, 7, 1), Units = 2, UnitPrice = 10, CashAmount = 20
        });
        context.MarketPriceBars.AddRange(
            new MarketPriceBar { Provider = "test", ExternalInstrumentId = "TEST|", Symbol = "TEST", MarketDate = new DateOnly(2026, 7, 20), Close = 10 },
            new MarketPriceBar { Provider = "test", ExternalInstrumentId = "TEST|", Symbol = "TEST", MarketDate = new DateOnly(2026, 7, 23), Close = 12 });
        // The rate moved between the two closes and again after them; none of that is the market.
        context.FxRateBars.AddRange(
            new FxRateBar { Provider = "test", BaseCurrency = "USD", QuoteCurrency = "MYR", MarketDate = DaysAgo(10), Rate = 4 },
            new FxRateBar { Provider = "test", BaseCurrency = "USD", QuoteCurrency = "MYR", MarketDate = DaysAgo(3), Rate = 5 },
            new FxRateBar { Provider = "test", BaseCurrency = "USD", QuoteCurrency = "MYR", MarketDate = DaysAgo(1), Rate = 3 });
        await context.SaveChangesAsync();

        var portfolio = await NewService(context).GetPortfolioAsync("all", CancellationToken.None);
        var holding = Assert.Single(portfolio.Holdings);

        // Two units up USD 2 each, at today's rate of 3.
        Assert.Equal(4m, holding.DailyChangeNative);
        Assert.Equal(12m, holding.DailyChangeApp);
        Assert.Equal(12m, portfolio.Summary.DailyChange);
        Assert.Equal(3m, holding.FxRate);
        Assert.Equal(72m, portfolio.Summary.MarketValue);
        // The cost is the USD paid at today's rate too, so the gain is the market's alone: +20%.
        Assert.Equal(60m, portfolio.Summary.CostBasis);
        Assert.Equal(12m, portfolio.Summary.UnrealisedProfitLoss);
        Assert.Equal(20m, portfolio.Summary.UnrealisedPercent);
        Assert.Equal(20m, holding.UnrealisedPercent);
        Assert.Equal(4m, holding.UnrealisedProfitLossNative);
    }

    [Fact]
    public async Task SwitchingTheAppCurrencyAndBackLeavesEveryFigureUnchanged()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var setting = new FinancialSetting { Currency = "MYR" };
        context.FinancialSettings.Add(setting);
        var (account, instrument) = await SeedUsdFund(context);
        context.InvestmentCashFlows.Add(new InvestmentCashFlow
        {
            AccountId = account.Id, Currency = "USD", Type = "Deposit", Amount = 30, Date = new DateOnly(2026, 7, 1)
        });
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
            Type = "Buy", TradeDate = new DateOnly(2026, 7, 1), Units = 2, UnitPrice = 10, CashAmount = 20
        });
        context.MarketPriceBars.Add(new MarketPriceBar
        {
            Provider = "test", ExternalInstrumentId = "TEST|", Symbol = "TEST", MarketDate = new DateOnly(2026, 7, 23), Close = 12
        });
        context.FxRateBars.AddRange(
            new FxRateBar { Provider = "test", BaseCurrency = "USD", QuoteCurrency = "MYR", MarketDate = DaysAgo(30), Rate = 4 },
            new FxRateBar { Provider = "test", BaseCurrency = "USD", QuoteCurrency = "MYR", MarketDate = DaysAgo(1), Rate = 5 });
        await context.SaveChangesAsync();

        var inRinggit = (await NewService(context).GetPortfolioAsync("all", CancellationToken.None)).Summary;
        setting.Currency = "USD";
        await context.SaveChangesAsync();
        var inDollars = (await NewService(context).GetPortfolioAsync("all", CancellationToken.None)).Summary;
        setting.Currency = "MYR";
        await context.SaveChangesAsync();
        var backInRinggit = (await NewService(context).GetPortfolioAsync("all", CancellationToken.None)).Summary;

        // In dollars, the fund's own figures: 2 x 12 held, 10 cash, 30 put in.
        Assert.Equal(34m, inDollars.TotalValue);
        Assert.Equal(30m, inDollars.NetDeposits);
        Assert.Equal(4m, inDollars.UnrealisedProfitLoss);
        // In ringgit, the same figures at today's rate of 5, whatever the rate was on 1 July.
        Assert.Equal(170m, inRinggit.TotalValue);
        Assert.Equal(150m, inRinggit.NetDeposits);
        Assert.Equal(20m, inRinggit.UnrealisedProfitLoss);
        Assert.Equal(inRinggit.UnrealisedPercent, inDollars.UnrealisedPercent);
        Assert.Equivalent(inRinggit, backInRinggit, strict: true);
        // The per-currency view never converts, so it does not move at all.
        Assert.Equivalent(inRinggit.Currencies.Select(value => value with { RateToAppCurrency = null, RateDate = null }),
            inDollars.Currencies.Select(value => value with { RateToAppCurrency = null, RateDate = null }), strict: true);
    }

    [Fact]
    public async Task AConversionCountsAsMoneyPutIntoTheCurrencyItBought()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "MYR" });
        var (account, instrument) = await SeedUsdFund(context);
        context.InvestmentCashFlows.AddRange(
            new InvestmentCashFlow
            {
                AccountId = account.Id, Currency = "MYR", Type = "Deposit", Amount = 1000, Date = new DateOnly(2026, 7, 1)
            },
            // RM1000 bought USD 220: a rate of 0.22, whatever today's rate is.
            new InvestmentCashFlow
            {
                AccountId = account.Id, Currency = "MYR", Type = "Conversion", Amount = -1000,
                ToCurrency = "USD", ToAmount = 220, Date = new DateOnly(2026, 7, 2)
            });
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument,
            Type = "Buy", TradeDate = new DateOnly(2026, 7, 3), Units = 2, UnitPrice = 100, CashAmount = 200
        });
        context.MarketPriceBars.Add(new MarketPriceBar
        {
            Provider = "test", ExternalInstrumentId = "TEST|", Symbol = "TEST", MarketDate = new DateOnly(2026, 7, 23), Close = 110
        });
        context.FxRateBars.Add(new FxRateBar
        {
            Provider = "test", BaseCurrency = "USD", QuoteCurrency = "MYR", MarketDate = DaysAgo(1), Rate = 4.5m
        });
        await context.SaveChangesAsync();

        var summary = (await NewService(context).GetPortfolioAsync("all", CancellationToken.None)).Summary;

        // USD 220 put in, USD 240 now (2 x 110 held + 20 cash): the gain is the market's USD 20.
        var dollars = Assert.Single(summary.Currencies);
        Assert.Equal("USD", dollars.Currency);
        Assert.Equal(220m, dollars.NetDeposits);
        Assert.Equal(240m, dollars.TotalValue);
        Assert.Equal(20m, dollars.UnrealisedProfitLoss);
        Assert.Equal(10m, dollars.UnrealisedPercent);
        Assert.Equal(4.5m, dollars.RateToAppCurrency);
        // What was sent to the broker is the RM1,000 deposit: the exchange inside it sends nothing,
        // so nothing shows as still waiting to be sent.
        Assert.Equal(1000m, summary.NetDeposits);
        Assert.Equal(1080m, summary.TotalValue);
    }

    [Fact]
    public async Task AMissingRateForAnOldCashMovementLeavesOnlyMoneyPutInUnknown()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "SGD" });
        var (account, _) = await SeedUsdFund(context);
        context.InvestmentCashFlows.AddRange(
            new InvestmentCashFlow
            {
                AccountId = account.Id, Currency = "MYR", Type = "Deposit", Amount = 1000, Date = new DateOnly(2026, 7, 1)
            },
            new InvestmentCashFlow
            {
                AccountId = account.Id, Currency = "MYR", Type = "Conversion", Amount = -1000,
                ToCurrency = "USD", ToAmount = 220, Date = new DateOnly(2026, 7, 2)
            });
        // Only USD can be valued today; the ringgit, now fully converted away, has no rate.
        context.FxRateBars.Add(new FxRateBar
        {
            Provider = "test", BaseCurrency = "USD", QuoteCurrency = "SGD", MarketDate = DaysAgo(1), Rate = 1.35m
        });
        await context.SaveChangesAsync();

        var portfolio = await NewService(context).GetPortfolioAsync("all", CancellationToken.None);

        Assert.Equal(297m, portfolio.Summary.TotalValue);
        Assert.Null(portfolio.Summary.NetDeposits);
        var latest = portfolio.Chart[^1];
        Assert.Equal(297m, latest.TotalValue);
        Assert.Null(latest.NetDeposits);
    }

    private static async Task<(InvestmentAccount Account, InvestmentInstrument Instrument)> SeedUsdFund(Database.AppDbContext context)
    {
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument
        {
            Symbol = "TEST", Name = "Test", Type = "Stock", Currency = "USD", ProviderSymbol = "TEST"
        };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();
        return (account, instrument);
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
    public async Task Cash_ReplaysTemporaryUsdOverdraftAndLaterConversionToExplicitZero()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "USD" });
        var account = new InvestmentAccount { Name = "Moomoo", BaseCurrency = "USD" };
        var voo = new InvestmentInstrument { Symbol = "VOO", Name = "Vanguard S&P 500", Type = "ETF", Currency = "USD", IsCustom = true };
        var vxus = new InvestmentInstrument { Symbol = "VXUS", Name = "Vanguard Total International", Type = "ETF", Currency = "USD", IsCustom = true };
        var bnd = new InvestmentInstrument { Symbol = "BND", Name = "Vanguard Total Bond", Type = "ETF", Currency = "USD", IsCustom = true };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.AddRange(voo, vxus, bnd);
        await context.SaveChangesAsync();

        context.InvestmentCashFlows.AddRange(
            new InvestmentCashFlow { AccountId = account.Id, Currency = "USD", Type = "Deposit", Amount = 0.26m, Date = new DateOnly(2026, 8, 1) },
            new InvestmentCashFlow { AccountId = account.Id, Currency = "MYR", Type = "Deposit", Amount = 572.04m, Date = new DateOnly(2026, 8, 1) },
            new InvestmentCashFlow
            {
                AccountId = account.Id, Currency = "MYR", Type = "Conversion", Amount = -572.04m,
                ToCurrency = "USD", ToAmount = 140.80m, Date = new DateOnly(2026, 8, 3)
            });
        context.InvestmentTransactions.AddRange(
            new InvestmentTransaction { AccountId = account.Id, InstrumentId = voo.Id, Instrument = voo, Type = "Buy", TradeDate = new DateOnly(2026, 8, 2), Units = 1, UnitPrice = 104.99m, CashAmount = 104.99m, Fees = 1.24m },
            new InvestmentTransaction { AccountId = account.Id, InstrumentId = vxus.Id, Instrument = vxus, Type = "Buy", TradeDate = new DateOnly(2026, 8, 2), Units = 1, UnitPrice = 10.99m, CashAmount = 10.99m, Fees = 0.36m },
            new InvestmentTransaction { AccountId = account.Id, InstrumentId = bnd.Id, Instrument = bnd, Type = "Buy", TradeDate = new DateOnly(2026, 8, 2), Units = 1, UnitPrice = 23m, CashAmount = 23m, Fees = 0.48m });
        await context.SaveChangesAsync();

        var portfolio = await NewService(context).GetPortfolioAsync("all", CancellationToken.None);

        Assert.Equal(0m, Assert.Single(portfolio.CashBalances, value => value.Currency == "USD").Amount);
        Assert.Equal(0m, Assert.Single(portfolio.CashBalances, value => value.Currency == "MYR").Amount);
        Assert.Equal(0m, portfolio.Summary.CashValue);
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
            MarketDate = DaysAgo(1),
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

    // Today's rate must be recent to stand for today (InvestmentConstantCurrency.RateLookbackDays).
    private static DateOnly DaysAgo(int days) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-days);

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
