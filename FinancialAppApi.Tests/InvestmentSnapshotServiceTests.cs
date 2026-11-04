using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Investments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

/// <summary>
/// Month-end investment snapshots are a cache over the activity: these pin the figures, that a
/// resumed rebuild equals a full replay, and that every way the source can change -- a backdated
/// edit, a reclassification, a currency switch, a newly arrived rate -- removes what it affects.
/// </summary>
public sealed class InvestmentSnapshotServiceTests
{
    private const decimal Rate = 1.35m;

    [Fact]
    public async Task SettledMonthsCarryContributionsValuesAndPerMarketFlows()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var seed = await SeedAsync(context);

        var rows = await NewService(context, new DateOnly(2031, 3, 20)).EnsureAsync(CancellationToken.None);

        Assert.Equal([new DateOnly(2031, 1, 31), new DateOnly(2031, 2, 28)],
            rows.Where(row => row.Segment == InvestmentSnapshotSegments.Total).Select(row => row.PeriodEnd).ToList());
        var january = Row(rows, 2031, 1, InvestmentSnapshotSegments.Total);
        Assert.Equal(0m, january.OpeningValue);
        Assert.Equal(1000m * Rate, january.NetContributions);
        Assert.Equal(Math.Round(1000m * Rate * 21 / 31, 10), january.WeightedContributions);
        Assert.Equal(5 * 110m * Rate, january.MarketValue);
        Assert.Equal(500m * Rate, january.CashValue);
        Assert.Equal(5 * 110m * Rate + 500m * Rate, january.ClosingValue);
        Assert.True(january.IsComplete);
        Assert.NotNull(InvestmentCarryState.TryParse(january.CarryState));
        var januaryUs = Row(rows, 2031, 1, "USEquity");
        Assert.Equal(500m * Rate, januaryUs.NetContributions);
        Assert.Equal(5 * 110m * Rate, januaryUs.ClosingValue);
        Assert.Null(januaryUs.CarryState);

        var february = Row(rows, 2031, 2, InvestmentSnapshotSegments.Total);
        Assert.Equal(january.ClosingValue, february.OpeningValue);
        Assert.Equal(0m, february.NetContributions);
        Assert.Equal((5 * 120m + 2 * 100m + 300m) * Rate, february.ClosingValue);
        var februaryBonds = Row(rows, 2031, 2, "Bonds");
        Assert.Equal(0m, februaryBonds.OpeningValue);
        Assert.Equal(200m * Rate, februaryBonds.NetContributions);
        Assert.Equal(seed.Bnd.Id, Assert.Single(InvestmentCarryState.TryParse(february.CarryState)!.Positions,
            position => position.InstrumentId == seed.Bnd.Id).InstrumentId);
    }

    [Fact]
    public async Task AMonthWhoseEndIsNotYetSettledIsNeverStored()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedAsync(context);

        var rows = await NewService(context, new DateOnly(2031, 3, 5)).EnsureAsync(CancellationToken.None);

        Assert.Equal([new DateOnly(2031, 1, 31)], rows.Select(row => row.PeriodEnd).Distinct().ToList());
    }

    [Fact]
    public async Task ResumingFromAStoredMonthMatchesAFullReplayWithoutReadingOlderMarketData()
    {
        await using var resumed = TestHelpers.NewInMemoryContext();
        await using var full = TestHelpers.NewInMemoryContext();
        foreach (var context in new[] { resumed, full })
        {
            var seed = await SeedAsync(context);
            await AddLaterActivityAsync(context, seed);
        }
        await NewService(resumed, new DateOnly(2031, 3, 20)).EnsureAsync(CancellationToken.None);
        // Prove the resume reads nothing before the stored month: older bars are gone.
        resumed.MarketPriceBars.RemoveRange(resumed.MarketPriceBars.Where(bar => bar.MarketDate < new DateOnly(2031, 1, 28)));
        resumed.FxRateBars.RemoveRange(resumed.FxRateBars.Where(bar => bar.MarketDate < new DateOnly(2031, 1, 28)));
        await resumed.SaveChangesAsync();

        var resumedRows = await NewService(resumed, new DateOnly(2031, 6, 20)).EnsureAsync(CancellationToken.None);
        var fullRows = await NewService(full, new DateOnly(2031, 6, 20)).EnsureAsync(CancellationToken.None);

        Assert.Equal(Figures(fullRows), Figures(resumedRows));
        Assert.All(resumedRows, row => Assert.True(row.IsComplete, row.MissingReason));
        Assert.Equal(new DateOnly(2031, 5, 31), resumedRows.Max(row => row.PeriodEnd));
    }

    [Fact]
    public async Task BackdatedActivityRemovesItsMonthAndEveryLaterOne()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var seed = await SeedAsync(context);
        await AddLaterActivityAsync(context, seed);
        await NewService(context, new DateOnly(2031, 6, 20)).EnsureAsync(CancellationToken.None);

        context.InvestmentTransactions.Add(Buy(seed, seed.Voo, new DateOnly(2031, 3, 2), 1, 100));
        await context.SaveChangesAsync();

        Assert.Equal([new DateOnly(2031, 1, 31), new DateOnly(2031, 2, 28)],
            context.InvestmentPeriodSnapshots.Select(row => row.PeriodEnd).Distinct().OrderBy(date => date).ToList());

        // Moving a later movement into January reaches back to January, the older of its two dates.
        var flow = context.InvestmentCashFlows.Single(value => value.Date == new DateOnly(2031, 4, 2));
        flow.Date = new DateOnly(2031, 1, 20);
        await context.SaveChangesAsync();

        Assert.Empty(context.InvestmentPeriodSnapshots);
    }

    [Fact]
    public async Task ReclassifyingAnInvestmentRemovesEveryMonth()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var seed = await SeedAsync(context);
        await NewService(context, new DateOnly(2031, 3, 20)).EnsureAsync(CancellationToken.None);

        seed.Bnd.AllocationSleeve = "InternationalExUS";
        await context.SaveChangesAsync();

        Assert.Empty(context.InvestmentPeriodSnapshots);
    }

    [Fact]
    public async Task SwitchingTheAppCurrencyRebuildsEveryMonthInTheNewCurrency()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedAsync(context);
        var service = NewService(context, new DateOnly(2031, 3, 20));
        await service.EnsureAsync(CancellationToken.None);

        context.FinancialSettings.Single().Currency = "USD";
        await context.SaveChangesAsync();
        var rows = await service.EnsureAsync(CancellationToken.None);

        Assert.All(rows, row => Assert.Equal("USD", row.AppCurrency));
        Assert.Equal((5 * 120m + 2 * 100m + 300m), Row(rows, 2031, 2, InvestmentSnapshotSegments.Total).ClosingValue);
        Assert.Equal(rows.Count, context.InvestmentPeriodSnapshots.Count());
    }

    [Fact]
    public async Task SwitchingTheMarketDataProviderRevaluesEveryMonthFromItsOwnData()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedAsync(context);
        await NewService(context, new DateOnly(2031, 3, 20)).EnsureAsync(CancellationToken.None);

        // The new provider has no bars yet: the months are rebuilt from its (empty) data, not kept.
        var rows = await NewService(context, new DateOnly(2031, 3, 20), "other").EnsureAsync(CancellationToken.None);

        Assert.All(rows, row => Assert.Equal("other", row.Provider));
        Assert.All(rows.Where(row => row.Segment == InvestmentSnapshotSegments.Total), row => Assert.False(row.IsComplete));
        Assert.DoesNotContain(context.InvestmentPeriodSnapshots, row => row.Provider == "test");
    }

    [Fact]
    public async Task AMissingRateLeavesTheMonthUnknownUntilNewMarketDataArrives()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedAsync(context, withRates: false);
        var service = NewService(context, new DateOnly(2031, 3, 20));

        var rows = await service.EnsureAsync(CancellationToken.None);

        var january = Row(rows, 2031, 1, InvestmentSnapshotSegments.Total);
        Assert.False(january.IsComplete);
        Assert.Null(january.ClosingValue);
        Assert.Null(january.NetContributions);
        Assert.Contains("USD/SGD", january.MissingReason);

        // Nothing new from the provider: the stored months are kept as they are.
        var computedAt = january.ComputedAt;
        await service.EnsureAsync(CancellationToken.None);
        Assert.Equal(computedAt, context.InvestmentPeriodSnapshots.Single(row =>
            row.PeriodEnd == january.PeriodEnd && row.Segment == InvestmentSnapshotSegments.Total).ComputedAt);

        // The rates arrive (the backfill records that the provider answered): the months complete.
        SeedRates(context);
        context.MarketSeriesCoverages.Add(new MarketSeriesCoverage
        {
            Provider = "test", Kind = MarketSeriesKinds.Fx, SeriesKey = "USD/SGD",
            CoveredFrom = new DateOnly(2031, 1, 1), CheckedAt = DateTime.UtcNow.AddMinutes(1),
        });
        await context.SaveChangesAsync();
        rows = await service.EnsureAsync(CancellationToken.None);

        Assert.All(rows, row => Assert.True(row.IsComplete, row.MissingReason));
        Assert.Equal(1000m * Rate, Row(rows, 2031, 1, InvestmentSnapshotSegments.Total).NetContributions);
    }

    [Fact]
    public async Task AnUnreadableCarryFallsBackToReplayingFromTheLastTrustedMonth()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedAsync(context);
        var service = NewService(context, new DateOnly(2031, 3, 20));
        var before = Figures(await service.EnsureAsync(CancellationToken.None));

        context.InvestmentPeriodSnapshots.Single(row =>
            row.PeriodEnd == new DateOnly(2031, 1, 31) && row.Segment == InvestmentSnapshotSegments.Total).CarryState = "{not json";
        await context.SaveChangesAsync();
        var after = Figures(await service.EnsureAsync(CancellationToken.None));

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task NoActivityStoresNothingAndClearsWhatWasThere()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { Currency = "SGD" });
        context.InvestmentPeriodSnapshots.Add(new InvestmentPeriodSnapshot
        {
            PeriodEnd = new DateOnly(2031, 1, 31), AppCurrency = "SGD", Provider = "test", SchemaVersion = InvestmentSnapshotService.SchemaVersion,
        });
        await context.SaveChangesAsync();

        var rows = await NewService(context, new DateOnly(2031, 3, 20)).EnsureAsync(CancellationToken.None);

        Assert.Empty(rows);
        Assert.Empty(context.InvestmentPeriodSnapshots);
    }

    private sealed record Seed(InvestmentAccount Account, InvestmentInstrument Voo, InvestmentInstrument Bnd);

    private static async Task<Seed> SeedAsync(AppDbContext context, bool withRates = true)
    {
        context.FinancialSettings.Add(new FinancialSetting { Currency = "SGD" });
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var voo = new InvestmentInstrument
        {
            Symbol = "VOO", Name = "Vanguard S&P 500 ETF", Type = "ETF", Currency = "USD",
            ProviderSymbol = "VOO", AllocationSleeve = "USEquity",
        };
        var bnd = new InvestmentInstrument
        {
            Symbol = "BND", Name = "Vanguard Total Bond ETF", Type = "ETF", Currency = "USD",
            ProviderSymbol = "BND", AllocationSleeve = "Bonds",
        };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.AddRange(voo, bnd);
        await context.SaveChangesAsync();
        var seed = new Seed(account, voo, bnd);

        context.InvestmentCashFlows.Add(Deposit(seed, new DateOnly(2031, 1, 10), 1000));
        context.InvestmentTransactions.Add(Buy(seed, voo, new DateOnly(2031, 1, 15), 5, 100));
        context.InvestmentTransactions.Add(Buy(seed, bnd, new DateOnly(2031, 2, 3), 2, 100));
        // VOO closes January at 110 and every later month at 120; BND is flat at 100.
        for (var date = new DateOnly(2031, 1, 2); date <= new DateOnly(2031, 6, 19); date = date.AddDays(1))
        {
            context.MarketPriceBars.Add(Bar("VOO|", date, date < new DateOnly(2031, 2, 1) ? 110m : 120m));
            context.MarketPriceBars.Add(Bar("BND|", date, 100m));
        }
        if (withRates) SeedRates(context);
        await context.SaveChangesAsync();
        return seed;
    }

    private static void SeedRates(AppDbContext context)
    {
        for (var date = new DateOnly(2031, 1, 2); date <= new DateOnly(2031, 6, 19); date = date.AddDays(1))
            context.FxRateBars.Add(new FxRateBar { Provider = "test", BaseCurrency = "USD", QuoteCurrency = "SGD", MarketDate = date, Rate = Rate });
    }

    // A sale, a dividend, a fee, a withdrawal, and a conversion: everything the carry must hold.
    private static async Task AddLaterActivityAsync(AppDbContext context, Seed seed)
    {
        context.InvestmentTransactions.AddRange(
            new InvestmentTransaction
            {
                AccountId = seed.Account.Id, InstrumentId = seed.Voo.Id, Instrument = seed.Voo, Type = "Sell",
                TradeDate = new DateOnly(2031, 3, 10), Units = 2, UnitPrice = 120, CashAmount = 240, Fees = 1,
            },
            new InvestmentTransaction
            {
                AccountId = seed.Account.Id, InstrumentId = seed.Bnd.Id, Instrument = seed.Bnd, Type = "Dividend",
                TradeDate = new DateOnly(2031, 4, 1), Units = 0, CashAmount = 6, Taxes = 1,
            },
            new InvestmentTransaction
            {
                AccountId = seed.Account.Id, InstrumentId = seed.Voo.Id, Instrument = seed.Voo, Type = "FeeTax",
                TradeDate = new DateOnly(2031, 4, 20), Units = 0, CashAmount = 2,
            },
            Buy(seed, seed.Voo, new DateOnly(2031, 5, 6), 1, 120));
        context.InvestmentCashFlows.AddRange(
            Deposit(seed, new DateOnly(2031, 4, 2), 400),
            new InvestmentCashFlow
            {
                AccountId = seed.Account.Id, Currency = "USD", Type = "Withdrawal", Amount = -50, Date = new DateOnly(2031, 5, 15),
            });
        await context.SaveChangesAsync();
    }

    private static InvestmentTransaction Buy(Seed seed, InvestmentInstrument instrument, DateOnly date, decimal units, decimal price) => new()
    {
        AccountId = seed.Account.Id, InstrumentId = instrument.Id, Instrument = instrument, Type = "Buy",
        TradeDate = date, Units = units, UnitPrice = price, CashAmount = units * price,
    };

    private static InvestmentCashFlow Deposit(Seed seed, DateOnly date, decimal amount) => new()
    {
        AccountId = seed.Account.Id, Currency = "USD", Type = "Deposit", Amount = amount, Date = date,
    };

    private static MarketPriceBar Bar(string externalId, DateOnly date, decimal close) => new()
    {
        Provider = "test", ExternalInstrumentId = externalId, Symbol = externalId.TrimEnd('|'), Mic = "", MarketDate = date, Close = close,
    };

    private static InvestmentPeriodSnapshot Row(IEnumerable<InvestmentPeriodSnapshot> rows, int year, int month, string segment)
        => rows.Single(row => row.PeriodEnd.Year == year && row.PeriodEnd.Month == month && row.Segment == segment);

    private static List<string> Figures(IEnumerable<InvestmentPeriodSnapshot> rows) => rows
        .OrderBy(row => row.PeriodEnd).ThenBy(row => row.Segment, StringComparer.Ordinal)
        .Select(row => string.Join("|", row.PeriodEnd, row.Segment, row.AppCurrency, row.OpeningValue, row.ClosingValue,
            row.MarketValue, row.CashValue, row.NetContributions, row.WeightedContributions, row.Dividends,
            row.RealisedProfitLoss, row.CostBasis, row.IsComplete, row.MissingReason))
        .ToList();

    private static InvestmentSnapshotService NewService(AppDbContext context, DateOnly today, string providerId = "test") => new(
        context,
        new InvestmentAccountingService(),
        new SnapshotProvider(providerId),
        new FinancialClock(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Financial:TimeZoneId"] = "UTC" }).Build(),
            new FixedTimeProvider(new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero))),
        NullLogger<InvestmentSnapshotService>.Instance);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SnapshotProvider(string providerId) : IMarketDataProvider
    {
        public MarketDataProviderDescriptor Descriptor => new(
            providerId, "Test data", true, MarketDataCapabilities.RequiredForActivation, new MarketDataQuotaPolicy(6, 750, 200));
        public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(MarketInstrumentReference instrument, DateOnly startDate, CancellationToken cancellationToken)
            => throw new NotSupportedException("Snapshots never call the provider.");
        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(string baseCurrency, string quoteCurrency, DateOnly startDate, CancellationToken cancellationToken)
            => throw new NotSupportedException("Snapshots never call the provider.");
        public MarketInstrumentReference? TryResolveLegacyReference(string? symbol, string? mic)
            => string.IsNullOrWhiteSpace(symbol) ? null : new(providerId, $"{symbol}|{mic}");
    }
}
