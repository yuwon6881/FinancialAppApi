using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FinancialAppApi.Tests;

public sealed class LedgerRetentionServiceTests
{
    // Retention exists to bound growth, so what it must never do is prune something a figure still
    // depends on. FX rate bars are the trap: cost basis values every trade at its own trade date,
    // so an eight-year-old trade still needs an eight-year-old rate. Pruning them would silently
    // turn a known cost basis into an unknown one, which is why only price bars are aged out.
    [Fact]
    public async Task PruneAsync_AgesOutPriceBarsAndAlertRecordsButNeverFxRates()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = NewContext(connection);
        await context.Database.EnsureCreatedAsync();

        context.AppUsers.Add(new AppUser
        {
            Id = TestHelpers.DefaultUserId,
            Username = "retention",
            NormalizedUsername = "RETENTION",
        });
        var longAgo = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-9);
        var recently = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);
        context.MarketPriceBars.AddRange(
            PriceBar("stale", longAgo),
            PriceBar("fresh", recently));
        context.FxRateBars.Add(new FxRateBar
        {
            Provider = "test",
            BaseCurrency = "USD",
            QuoteCurrency = "MYR",
            MarketDate = longAgo,
            Rate = 4.2m,
        });
        context.CategoryLimitAlertEvaluations.AddRange(
            Evaluation("old", DateTime.UtcNow.AddYears(-2)),
            Evaluation("recent", DateTime.UtcNow.AddDays(-1)));
        await context.SaveChangesAsync();

        var result = await NewService(context).PruneAsync();

        Assert.Equal(1, result.PriceBars);
        Assert.Equal(1, result.AlertEvaluations);

        // The bar that is still inside every supported chart range survives, and so does every
        // FX rate regardless of age.
        Assert.Equal(["fresh"], context.MarketPriceBars.Select(bar => bar.Symbol).ToList());
        Assert.Single(context.FxRateBars);
        Assert.Equal(["recent"], context.CategoryLimitAlertEvaluations.IgnoreQueryFilters()
            .Select(evaluation => evaluation.Id).ToList());
    }

    [Fact]
    public async Task PruneAsync_DoesNothingWhenRetentionIsDisabled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = NewContext(connection);
        await context.Database.EnsureCreatedAsync();
        context.MarketPriceBars.Add(PriceBar("stale", DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-9)));
        await context.SaveChangesAsync();

        var result = await NewService(context, enabled: false).PruneAsync();

        Assert.Equal(0, result.Total);
        Assert.Single(context.MarketPriceBars);
    }

    private static AppDbContext NewContext(SqliteConnection connection)
    {
        var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        context.SetCurrentUser(TestHelpers.DefaultUserId);
        return context;
    }

    private static LedgerRetentionService NewService(AppDbContext context, bool enabled = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Retention:Enabled"] = enabled ? "true" : "false",
                ["Financial:TimeZoneId"] = "UTC",
            })
            .Build();
        return new LedgerRetentionService(
            context,
            new LedgerRetentionPolicy(configuration),
            new FinancialClock(configuration));
    }

    private static MarketPriceBar PriceBar(string symbol, DateOnly date) => new()
    {
        Provider = "test",
        ExternalInstrumentId = symbol,
        Symbol = symbol,
        MarketDate = date,
        Close = 10m,
    };

    private static CategoryLimitAlertEvaluation Evaluation(string id, DateTime createdAt) => new()
    {
        Id = id,
        UserId = TestHelpers.DefaultUserId,
        CreatedAt = createdAt,
    };
}
