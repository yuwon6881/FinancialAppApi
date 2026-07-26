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

    [Fact]
    public async Task RefreshDropsPendingItemForDeletedInstrumentInsteadOfThrowing()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (account, instrument) = await SeedProviderBackedHoldingAsync(context);
        var deletedId = Guid.NewGuid();
        context.MarketDataRefreshJobs.Add(new MarketDataRefreshJob
        {
            Status = "Pending",
            ReportingCurrency = "USD",
            TotalItems = 1,
            PendingItemsJson = $"[\"instrument:{deletedId}\"]"
        });
        await context.SaveChangesAsync();
        var service = NewService(context, new CountingProvider());

        var result = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal("Complete", result.Status);
        var job = context.MarketDataRefreshJobs.Single();
        Assert.Equal("[]", job.PendingItemsJson);
        Assert.NotNull(account);
        Assert.NotNull(instrument);
    }

    [Fact]
    public async Task RefreshDropsMalformedPendingItem()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedProviderBackedHoldingAsync(context);
        context.MarketDataRefreshJobs.Add(new MarketDataRefreshJob
        {
            Status = "Pending",
            ReportingCurrency = "USD",
            TotalItems = 1,
            PendingItemsJson = "[\"fx:USD\"]"
        });
        await context.SaveChangesAsync();
        var service = NewService(context, new CountingProvider());

        var result = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal("Complete", result.Status);
        Assert.Equal("[]", context.MarketDataRefreshJobs.Single().PendingItemsJson);
    }

    [Fact]
    public async Task TransientProviderFailureRequeuesItemAndDoesNotCompleteTheJob()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (_, instrument) = await SeedProviderBackedHoldingAsync(context);
        context.MarketDataRefreshJobs.Add(new MarketDataRefreshJob
        {
            Status = "Pending",
            ReportingCurrency = "USD",
            TotalItems = 1,
            PendingItemsJson = $"[\"instrument:{instrument.Id}\"]"
        });
        await context.SaveChangesAsync();
        var service = NewService(context, new ThrowingProvider());

        var result = await service.RefreshAsync(CancellationToken.None);

        Assert.NotEqual("Complete", result.Status);
        Assert.Equal(0, result.Updated);
        var job = context.MarketDataRefreshJobs.Single();
        Assert.Contains("attempts=1", job.PendingItemsJson);
        Assert.Contains(instrument.Id.ToString(), job.PendingItemsJson);
    }

    [Fact]
    public async Task EmptyProviderSeriesAdvancesFreshnessSoTheNextRefreshMakesNoCall()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (_, instrument) = await SeedProviderBackedHoldingAsync(context);
        // A previously fetched bar exists but is stale, so the gate lets the call through.
        context.MarketPriceBars.Add(new MarketPriceBar
        {
            Symbol = instrument.ProviderSymbol!,
            Mic = "",
            MarketDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3)),
            Close = 100m,
            FetchedAt = DateTime.UtcNow.AddDays(-3)
        });
        await context.SaveChangesAsync();
        var provider = new CountingProvider();
        var service = NewService(context, provider);

        var first = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, provider.PriceCallCount);
        Assert.Contains(first.Warnings, warning => warning.Contains(instrument.Symbol, StringComparison.Ordinal));
        var bar = context.MarketPriceBars.Single();
        Assert.True(bar.FetchedAt > DateTime.UtcNow.AddMinutes(-1));

        await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, provider.PriceCallCount);
    }

    [Fact]
    public async Task PerUserDailyCeilingDoesNotConsumeAnotherUsersAllowance()
    {
        await using var context = TestHelpers.NewInMemoryContext("alice");
        context.AppUsers.AddRange(
            new AppUser { Id = "alice", Username = "alice", NormalizedUsername = "ALICE", PasswordHash = "x" },
            new AppUser { Id = "bob", Username = "bob", NormalizedUsername = "BOB", PasswordHash = "x" });
        await context.SaveChangesAsync();
        var dayStart = DateTime.UtcNow.Date;
        // Alice has already spent her per-user allowance for the day.
        context.MarketDataQuotaWindows.Add(new MarketDataQuotaWindow
        {
            Scope = "user-day:alice",
            WindowStart = dayStart,
            Used = 5
        });
        await context.SaveChangesAsync();
        var aliceInstrument = (await SeedProviderBackedHoldingAsync(context)).Instrument;
        var options = Options.Create(new MarketDataOptions { PerUserDailyCallCeiling = 5 });
        var provider = new CountingProvider();
        var service = new InvestmentMarketDataService(
            context, provider, options, NullLogger<InvestmentMarketDataService>.Instance);

        var aliceResult = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(0, provider.PriceCallCount);
        Assert.NotEqual("Complete", aliceResult.Status);
        Assert.NotNull(aliceInstrument);

        context.SetCurrentUser("bob");
        await SeedProviderBackedHoldingAsync(context);
        var bobService = new InvestmentMarketDataService(
            context, provider, options, NullLogger<InvestmentMarketDataService>.Instance);

        await bobService.RefreshAsync(CancellationToken.None);

        // Bob's own window is untouched, so his refresh still reaches the provider.
        Assert.True(provider.PriceCallCount > 0);
        Assert.Equal(5, context.MarketDataQuotaWindows
            .Single(window => window.Scope == "user-day:alice").Used);
    }

    [Fact]
    public async Task QuotaWindowsOlderThanADayArePruned()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedProviderBackedHoldingAsync(context);
        context.MarketDataQuotaWindows.Add(new MarketDataQuotaWindow
        {
            Scope = "provider-minute",
            WindowStart = DateTime.UtcNow.AddDays(-3),
            Used = 4
        });
        await context.SaveChangesAsync();
        var service = NewService(context, new CountingProvider());

        await service.RefreshAsync(CancellationToken.None);

        Assert.DoesNotContain(
            context.MarketDataQuotaWindows.ToList(),
            window => window.WindowStart < DateTime.UtcNow.AddDays(-1));
    }

    [Fact]
    public async Task LowercaseInstrumentCurrencyIsNormalisedOnWrite()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var instrument = new InvestmentInstrument
        {
            Symbol = "VOO",
            Name = "Vanguard S&P 500 ETF",
            Type = "ETF",
            Currency = "usd"
        };
        context.InvestmentInstruments.Add(instrument);
        context.InvestmentAccounts.Add(new InvestmentAccount { Name = "Broker", BaseCurrency = "myr" });
        await context.SaveChangesAsync();

        Assert.Equal("USD", context.InvestmentInstruments.Single().Currency);
        Assert.Equal("MYR", context.InvestmentAccounts.Single().BaseCurrency);
    }

    private static InvestmentMarketDataService NewService(
        Database.AppDbContext context,
        IMarketDataProvider provider) => new(
        context,
        provider,
        Options.Create(new MarketDataOptions()),
        NullLogger<InvestmentMarketDataService>.Instance);

    private static async Task<(InvestmentAccount Account, InvestmentInstrument Instrument)> SeedProviderBackedHoldingAsync(
        Database.AppDbContext context)
    {
        if (!context.FinancialSettings.Any())
        {
            context.FinancialSettings.Add(new FinancialSetting { Currency = "USD" });
        }
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument
        {
            Symbol = "VOO",
            Name = "Vanguard S&P 500 ETF",
            Type = "ETF",
            Currency = "USD",
            ProviderSymbol = "VOO"
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
            TradeDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10)),
            Units = 10,
            UnitPrice = 100,
            CashAmount = 1000
        });
        await context.SaveChangesAsync();
        return (account, instrument);
    }

    private sealed class ThrowingProvider : IMarketDataProvider
    {
        public bool IsConfigured => true;

        public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
            => throw new MarketDataProviderException("Provider is briefly unavailable.", MarketDataFailure.Timeout);

        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(string symbol, string? mic, DateOnly startDate, CancellationToken cancellationToken)
            => throw new MarketDataProviderException("Provider is briefly unavailable.", MarketDataFailure.Timeout);

        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(string baseCurrency, string quoteCurrency, DateOnly startDate, CancellationToken cancellationToken)
            => throw new MarketDataProviderException("Provider is briefly unavailable.", MarketDataFailure.Timeout);
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
        public int PriceCallCount { get; private set; }
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
            PriceCallCount++;
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
