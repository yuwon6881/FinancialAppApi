using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests;

public sealed class InvestmentPortfolioServiceTests
{
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
    public async Task Buy_ReducesCash_WhileOpeningPositionDoesNot()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument { Symbol = "AAPL", Name = "Apple", Type = "Stock", Currency = "USD", IsCustom = true };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();

        context.InvestmentCashFlows.Add(new InvestmentCashFlow { AccountId = account.Id, Currency = "USD", Type = "Deposit", Amount = 1000, Date = new DateOnly(2025, 1, 1) });
        context.InvestmentTransactions.AddRange(
            new InvestmentTransaction { AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument, Type = "OpeningPosition", TradeDate = new DateOnly(2025, 1, 1), Units = 5, UnitPrice = 10, CashAmount = 50 },
            new InvestmentTransaction { AccountId = account.Id, InstrumentId = instrument.Id, Instrument = instrument, Type = "Buy", TradeDate = new DateOnly(2025, 2, 1), Units = 2, UnitPrice = 10, CashAmount = 20, Fees = 1 });
        await context.SaveChangesAsync();

        var portfolio = await NewService(context).GetPortfolioAsync("all", CancellationToken.None);

        var balance = Assert.Single(portfolio.CashBalances);
        // deposit 1000; opening position moves no cash; buy costs 20 + 1 fee => 979
        Assert.Equal(979m, balance.Amount);
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
