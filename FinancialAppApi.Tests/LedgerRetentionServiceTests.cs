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

    // Clearing a browser's site data destroys the device id push subscriptions are keyed by, so
    // the next enrolment inserts a new row and the old one can never be matched again. Without a
    // sweep the table gained a permanent row per shred -- and both the status read and the
    // spending-alert consent mirror materialize every row for the user on each write.
    [Fact]
    public async Task PruneAsync_AgesOutDisabledPushSubscriptionsButKeepsEnabledOnes()
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
        var longAgo = DateTime.UtcNow.AddYears(-2);
        context.PushSubscriptions.AddRange(
            Subscription("abandoned", "device-a", enabled: false, updatedAt: longAgo),
            // Enabled is a live device however long ago it last changed: a phone that simply had
            // nothing to be notified about must not be unsubscribed behind the user's back.
            Subscription("live-but-quiet", "device-b", enabled: true, updatedAt: longAgo),
            Subscription("recently-off", "device-c", enabled: false, updatedAt: DateTime.UtcNow.AddDays(-2)));
        await context.SaveChangesAsync();

        var result = await NewService(context).PruneAsync();

        Assert.Equal(1, result.PushSubscriptions);
        Assert.Equal(
            ["live-but-quiet", "recently-off"],
            context.PushSubscriptions.IgnoreQueryFilters().Select(s => s.Id).OrderBy(id => id).ToList());
    }

    // The soft-disable exists so the delivery ledgers' "already sent" claims stay addressable by
    // subscription id. Collecting a row out from under a surviving claim is exactly the
    // double-send it was guarding against, and the two horizons are configured independently, so
    // the sweep cannot assume the claims aged out first.
    [Fact]
    public async Task PruneAsync_KeepsDisabledPushSubscriptionStillNamedByADeliveryClaim()
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
        var longAgo = DateTime.UtcNow.AddYears(-2);
        context.PushSubscriptions.AddRange(
            Subscription("claimed-by-reminder", "device-a", enabled: false, updatedAt: longAgo),
            Subscription("claimed-by-alert", "device-b", enabled: false, updatedAt: longAgo),
            Subscription("unreferenced", "device-c", enabled: false, updatedAt: longAgo));
        context.PushReminderDeliveries.Add(new PushReminderDelivery
        {
            Id = "reminder-claim",
            UserId = TestHelpers.DefaultUserId,
            RecurringPaymentId = "rp-1",
            OccurrenceDate = DateOnly.FromDateTime(DateTime.UtcNow),
            ActualOffsetDays = 0,
            SubscriptionId = "claimed-by-reminder",
            // Inside the notification horizon, so the claim survives this same pass.
            SentAt = DateTime.UtcNow.AddDays(-1),
        });
        // Recent enough to outlive this pass, so the claim hanging off it does too.
        context.CategoryLimitAlertEvents.Add(new CategoryLimitAlertEvent
        {
            Id = "event-1",
            UserId = TestHelpers.DefaultUserId,
            CycleKey = "2026-09",
            Title = "Groceries",
            Body = "You are close to your guide.",
            Tag = "category-limit:groceries",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        });
        context.CategoryLimitAlertDeliveries.Add(new CategoryLimitAlertDelivery
        {
            Id = "alert-claim",
            UserId = TestHelpers.DefaultUserId,
            EventId = "event-1",
            SubscriptionId = "claimed-by-alert",
            SentAt = DateTime.UtcNow.AddDays(-1),
        });
        await context.SaveChangesAsync();

        var result = await NewService(context).PruneAsync();

        Assert.Equal(1, result.PushSubscriptions);
        Assert.Equal(
            ["claimed-by-alert", "claimed-by-reminder"],
            context.PushSubscriptions.IgnoreQueryFilters().Select(s => s.Id).OrderBy(id => id).ToList());
    }

    private static PushSubscription Subscription(string id, string deviceId, bool enabled, DateTime updatedAt) => new()
    {
        Id = id,
        UserId = TestHelpers.DefaultUserId,
        DeviceId = deviceId,
        FcmToken = enabled ? "token" : string.Empty,
        Enabled = enabled,
        BillRemindersEnabled = enabled,
        CategoryAlertsEnabled = false,
        CreatedAt = updatedAt,
        UpdatedAt = updatedAt,
    };

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
