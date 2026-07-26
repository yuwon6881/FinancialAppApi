using System.Net;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FinancialAppApi.Tests;

public sealed class InvestmentMarketDataTests
{
    [Fact]
    public async Task Provider_UsesAuthorizationHeaderAndNeverPutsKeyInUrl()
    {
        const string key = "test-secret-key";
        HttpRequestMessage? captured = null;
        var handler = new DelegateHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"data":[{"symbol":"VOO","instrument_name":"Vanguard S&P 500 ETF","instrument_type":"ETF","currency":"USD","mic_code":"ARCX"}]}
                    """)
            };
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test") };
        var provider = new TwelveDataMarketDataProvider(client, Options.Create(new MarketDataOptions
        {
            TwelveDataApiKey = key
        }));

        var result = await provider.SearchAsync("VOO", CancellationToken.None);

        Assert.Single(result);
        Assert.NotNull(captured);
        Assert.Equal("apikey", captured.Headers.Authorization?.Scheme);
        Assert.Equal(key, captured.Headers.Authorization?.Parameter);
        Assert.DoesNotContain(key, captured.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyPortfolioRefreshIsNoOpAndMakesNoProviderCalls()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "MYR" });
        await context.SaveChangesAsync();
        var provider = new CountingProvider();
        var service = new InvestmentMarketDataService(
            context,
            provider,
            Options.Create(new MarketDataOptions()),
            NullLogger<InvestmentMarketDataService>.Instance);

        var result = await service.RefreshAsync(CancellationToken.None);

        Assert.True(result.Complete);
        Assert.Equal(0, result.Total);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task PortfolioRefresh_LoadsDedicatedUsdDisplayRate_WhenHoldingsUseReportingCurrency()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "MYR" });
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "MYR" };
        var instrument = new InvestmentInstrument
        {
            Symbol = "LOCAL",
            Name = "Local fund",
            Type = "MutualFund",
            Currency = "MYR",
            IsCustom = true
        };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            AccountId = account.Id,
            InstrumentId = instrument.Id,
            Instrument = instrument,
            Type = "Buy",
            TradeDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Units = 10,
            UnitPrice = 1,
            CashAmount = 10
        });
        await context.SaveChangesAsync();
        var provider = new CountingProvider();
        var service = new InvestmentMarketDataService(
            context,
            provider,
            Options.Create(new MarketDataOptions()),
            NullLogger<InvestmentMarketDataService>.Instance);

        var result = await service.RefreshAsync(CancellationToken.None);

        Assert.True(result.Complete);
        Assert.Contains(("USD", "MYR"), provider.FxPairs);
    }

    [Fact]
    public async Task UserOwnedInvestmentRowsAreIsolatedByQueryFilter()
    {
        await using var context = TestHelpers.NewInMemoryContext("alice");
        context.AppUsers.AddRange(
            new AppUser { Id = "alice", Username = "alice", NormalizedUsername = "ALICE", PasswordHash = "x" },
            new AppUser { Id = "bob", Username = "bob", NormalizedUsername = "BOB", PasswordHash = "x" });
        context.InvestmentAccounts.Add(new InvestmentAccount { Name = "Alice broker", BaseCurrency = "USD" });
        await context.SaveChangesAsync();
        context.SetCurrentUser("bob");
        context.InvestmentAccounts.Add(new InvestmentAccount { Name = "Bob broker", BaseCurrency = "USD" });
        await context.SaveChangesAsync();

        var visible = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            context.InvestmentAccounts.AsQueryable());

        var account = Assert.Single(visible);
        Assert.Equal("Bob broker", account.Name);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private sealed class CountingProvider : IMarketDataProvider
    {
        public int CallCount { get; private set; }
        public List<(string Base, string Quote)> FxPairs { get; } = [];
        public bool IsConfigured => true;

        public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<InstrumentSearchResult>>([]);
        }

        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(string symbol, string? mic, DateOnly startDate, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<ProviderPriceBar>>([]);
        }

        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(string baseCurrency, string quoteCurrency, DateOnly startDate, CancellationToken cancellationToken)
        {
            CallCount++;
            FxPairs.Add((baseCurrency, quoteCurrency));
            return Task.FromResult<IReadOnlyList<ProviderFxBar>>(
                [new ProviderFxBar(DateOnly.FromDateTime(DateTime.UtcNow), 4.2m)]);
        }
    }
}
