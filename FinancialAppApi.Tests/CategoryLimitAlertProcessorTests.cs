using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class CategoryLimitAlertProcessorTests
{
    [Fact]
    public async Task ProcessPendingAsync_SendsNearAndTerminalOnce_WithoutRepeatingWhileOver()
    {
        var dbName = NewDbName();
        await SeedAsync(dbName, ("Dining", 100m, 79m));
        await using var context = NewContext(dbName, authenticated: true);
        var sender = new FakeSender();
        var processor = NewProcessor(context, sender);

        await AddExpenseAsync(context, "tx-near", "Dining", 1m);
        await processor.ProcessPendingAsync();
        await AddExpenseAsync(context, "tx-still-near", "Dining", 5m);
        await processor.ProcessPendingAsync();
        await AddExpenseAsync(context, "tx-limit", "Dining", 15m);
        await processor.ProcessPendingAsync();
        await AddExpenseAsync(context, "tx-over", "Dining", 1m);
        await processor.ProcessPendingAsync();

        Assert.Equal(2, sender.Sent.Count);
        Assert.Contains(sender.Sent, item => item.Content.Body == "Dining is close to what you planned to spend on it this cycle.");
        Assert.Contains(sender.Sent, item => item.Content.Body == "Dining has reached what you planned to spend on it this cycle.");
        Assert.Equal(2, await context.CategoryLimitAlertMilestones.CountAsync());
    }

    [Fact]
    public async Task ProcessPendingAsync_DirectJumpSendsOnlyExceededMilestone()
    {
        var dbName = NewDbName();
        await SeedAsync(dbName, ("Dining", 100m, 70m));
        await using var context = NewContext(dbName, authenticated: true);
        var sender = new FakeSender();

        await AddExpenseAsync(context, "tx-jump", "Dining", 35m);
        await NewProcessor(context, sender).ProcessPendingAsync();

        var content = Assert.Single(sender.Sent).Content;
        Assert.Equal("Dining has gone past what you planned to spend on it this cycle.", content.Body);
        var milestone = Assert.Single(await context.CategoryLimitAlertMilestones.ToListAsync());
        Assert.Equal("Limit", milestone.Milestone);
    }

    [Fact]
    public async Task ProcessPendingAsync_CategoryEditMovesSpendAndAlertsTheNewCategory()
    {
        var dbName = NewDbName();
        await SeedAsync(dbName, ("Entertainment", 100m, 21m), ("Hobbies", 100m, 79m));
        await using var context = NewContext(dbName, authenticated: true);
        var sender = new FakeSender();

        var transaction = await context.Transactions.SingleAsync(item => item.Id == "seed-Entertainment");
        transaction.Category = "Hobbies";
        await context.SaveChangesAsync();
        await NewProcessor(context, sender).ProcessPendingAsync();

        var content = Assert.Single(sender.Sent).Content;
        Assert.Contains("Hobbies", content.Body);
        Assert.DoesNotContain("Entertainment", content.Body);
        Assert.Equal("Hobbies", Assert.Single(await context.CategoryLimitAlertMilestones.ToListAsync()).CategoryName);
    }

    [Fact]
    public async Task ProcessPendingAsync_DoesNotRepeatAfterFallingBelowAndRecrossing()
    {
        var dbName = NewDbName();
        await SeedAsync(dbName, ("Dining", 100m, 79m));
        await using var context = NewContext(dbName, authenticated: true);
        var sender = new FakeSender();
        var processor = NewProcessor(context, sender);

        await AddExpenseAsync(context, "tx-cross", "Dining", 1m);
        await processor.ProcessPendingAsync();

        var transaction = await context.Transactions.FindAsync("tx-cross");
        context.Transactions.Remove(transaction!);
        await context.SaveChangesAsync();
        await processor.ProcessPendingAsync();

        await AddExpenseAsync(context, "tx-recross", "Dining", 1m);
        await processor.ProcessPendingAsync();

        Assert.Single(sender.Sent);
        Assert.Single(await context.CategoryLimitAlertMilestones.ToListAsync());
    }

    [Fact]
    public async Task ProcessPendingAsync_CoalescesMultipleCategoriesIntoOneNotification()
    {
        var dbName = NewDbName();
        await SeedAsync(dbName, ("Dining", 100m, 79m), ("Travel", 50m, 39m));
        await using var context = NewContext(dbName, authenticated: true);
        var sender = new FakeSender();

        context.Transactions.Add(NewExpense("tx-dining", "Dining", 1m));
        context.Transactions.Add(NewExpense("tx-travel", "Travel", 1m));
        await context.SaveChangesAsync();
        await NewProcessor(context, sender).ProcessPendingAsync();

        var content = Assert.Single(sender.Sent).Content;
        Assert.Equal("2 categories reached a point you asked to be told about.", content.Body);
        Assert.Equal("category-limit", content.Kind);
        Assert.Equal(2, await context.CategoryLimitAlertMilestones.CountAsync());
    }

    [Fact]
    public async Task ProcessPendingAsync_DropsEvaluationsWhenCategoryAlertsAreDisabled()
    {
        var dbName = NewDbName();
        await SeedAsync(dbName, [("Dining", 100m, 79m)], categoryAlertsEnabled: false);
        await using var context = NewContext(dbName, authenticated: true);
        var sender = new FakeSender();

        await AddExpenseAsync(context, "tx-cross", "Dining", 1m);
        await NewProcessor(context, sender).ProcessPendingAsync();

        Assert.Empty(sender.Sent);
        Assert.Empty(await context.CategoryLimitAlertEvaluations.ToListAsync());
        Assert.Empty(await context.CategoryLimitAlertEvents.ToListAsync());
    }

    [Fact]
    public async Task ProcessPendingAsync_SendsOnlyToDevicesOptedIntoSpendingAlerts()
    {
        // The reported multi-device failure: alerts turned on from a phone must not arrive on a
        // desktop that only asked for bill reminders.
        var dbName = NewDbName();
        await SeedAsync(dbName, [("Dining", 100m, 79m)], categoryAlertsEnabled: true);
        await using (var setup = NewContext(dbName, authenticated: true))
        {
            setup.PushSubscriptions.Add(new PushSubscription
            {
                Id = "push-2",
                UserId = "user-a",
                DeviceId = "device-2",
                FcmToken = "token-2",
                Enabled = true,
                BillRemindersEnabled = true,
                CategoryAlertsEnabled = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await setup.SaveChangesAsync();
        }

        await using var context = NewContext(dbName, authenticated: true);
        var sender = new FakeSender();

        await AddExpenseAsync(context, "tx-cross", "Dining", 1m);
        await NewProcessor(context, sender).ProcessPendingAsync();

        Assert.Equal("token-1", Assert.Single(sender.Sent).Token);
    }

    [Fact]
    public async Task ProcessPendingAsync_DoesNotSpendMilestonesWhenNoDeviceCanReceiveThem()
    {
        // The milestone rows are the once-per-cycle latch. Claiming one for an alert that is
        // immediately discarded for want of a device meant re-enabling push later in the cycle
        // produced silence for every category that had already crossed.
        var dbName = NewDbName();
        await SeedAsync(dbName, [("Dining", 100m, 79m)], categoryAlertsEnabled: true);
        await using (var setup = NewContext(dbName, authenticated: true))
        {
            var subscription = await setup.PushSubscriptions.SingleAsync();
            subscription.Enabled = false;
            await setup.SaveChangesAsync();
        }

        await using var context = NewContext(dbName, authenticated: true);
        var sender = new FakeSender();

        await AddExpenseAsync(context, "tx-cross", "Dining", 1m);
        await NewProcessor(context, sender).ProcessPendingAsync();

        Assert.Empty(sender.Sent);
        Assert.Empty(await context.CategoryLimitAlertEvaluations.ToListAsync());
        Assert.Empty(await context.CategoryLimitAlertEvents.ToListAsync());
        Assert.Empty(await context.CategoryLimitAlertMilestones.ToListAsync());
    }

    [Fact]
    public async Task ProcessPendingAsync_ReleasesMilestonesWhenAnUnconsentedEventIsDropped()
    {
        var dbName = NewDbName();
        await SeedAsync(dbName, [("Dining", 100m, 79m)], categoryAlertsEnabled: false);
        await using (var setup = NewContext(dbName, authenticated: true))
        {
            setup.CategoryLimitAlertEvents.Add(new CategoryLimitAlertEvent
            {
                Id = "event-dropped",
                UserId = "user-a",
                CycleKey = "2026-08",
                Title = "Category spending alert",
                Body = "Dining has reached your guide.",
                Tag = "category-limits:2026-08",
                CategoryName = "Dining",
                CreatedAt = CurrentDate,
                ExpiresAt = CurrentDate.AddDays(1)
            });
            setup.CategoryLimitAlertMilestones.Add(new CategoryLimitAlertMilestone
            {
                Id = "milestone-dropped",
                UserId = "user-a",
                EventId = "event-dropped",
                CycleKey = "2026-08",
                CategoryName = "Dining",
                Milestone = "Limit"
            });
            await setup.SaveChangesAsync();
        }

        await using var context = NewContext(dbName, authenticated: true);
        await NewProcessor(context, new FakeSender()).ProcessPendingAsync();

        Assert.Empty(await context.CategoryLimitAlertMilestones.ToListAsync());
        Assert.NotNull((await context.CategoryLimitAlertEvents.SingleAsync()).CompletedAt);
    }

    [Fact]
    public async Task ProcessPendingAsync_InvalidOnlyTokenWaitsForRenewalWithoutSpendingTheAlert()
    {
        var dbName = NewDbName();
        await SeedAsync(dbName, ("Dining", 100m, 79m));
        await using var context = NewContext(dbName, authenticated: true);
        var sender = new FakeSender { Result = new FcmSendResult(FcmSendStatus.InvalidOrUnregistered) };
        var processor = NewProcessor(context, sender);

        await AddExpenseAsync(context, "tx-cross", "Dining", 1m);
        await processor.ProcessPendingAsync();

        var failedEvent = await context.CategoryLimitAlertEvents.SingleAsync();
        Assert.True(failedEvent.AwaitingDeviceRecovery);
        Assert.Null(failedEvent.CompletedAt);
        Assert.Empty(await context.CategoryLimitAlertDeliveries.ToListAsync());
        Assert.Single(await context.CategoryLimitAlertMilestones.ToListAsync());
        Assert.False((await context.PushSubscriptions.SingleAsync()).Enabled);
        Assert.False((await context.FinancialSettings.SingleAsync()).CategoryLimitAlertsEnabled);

        sender.Result = new FcmSendResult(FcmSendStatus.Sent);
        var subscriptionService = new PushSubscriptionService(context, new FixedTimeProvider(CurrentDate.AddHours(2)));
        await subscriptionService.SubscribeAsync("device-1", "token-renewed", false, true);
        await processor.ProcessPendingAsync();

        var deliveredEvent = await context.CategoryLimitAlertEvents.SingleAsync();
        Assert.False(deliveredEvent.AwaitingDeviceRecovery);
        Assert.NotNull(deliveredEvent.CompletedAt);
        Assert.Single(await context.CategoryLimitAlertDeliveries.ToListAsync());
        Assert.Equal("token-renewed", sender.Sent.Last().Token);
    }

    [Fact]
    public async Task ProcessPendingAsync_InvalidTokenDoesNotDelayAnotherDevicesSuccessfulAlert()
    {
        var dbName = NewDbName();
        await SeedAsync(dbName, ("Dining", 100m, 79m));
        await using var context = NewContext(dbName, authenticated: true);
        context.PushSubscriptions.Add(new PushSubscription
        {
            Id = "push-2",
            DeviceId = "device-2",
            FcmToken = "token-good",
            Enabled = true,
            CategoryAlertsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var sender = new FakeSender
        {
            ResultForToken = token => token == "token-1"
                ? new FcmSendResult(FcmSendStatus.InvalidOrUnregistered)
                : new FcmSendResult(FcmSendStatus.Sent)
        };

        await AddExpenseAsync(context, "tx-cross", "Dining", 1m);
        await NewProcessor(context, sender).ProcessPendingAsync();

        var alertEvent = await context.CategoryLimitAlertEvents.SingleAsync();
        Assert.False(alertEvent.AwaitingDeviceRecovery);
        Assert.NotNull(alertEvent.CompletedAt);
        Assert.Equal("push-2", (await context.CategoryLimitAlertDeliveries.SingleAsync()).SubscriptionId);
        Assert.False((await context.PushSubscriptions.SingleAsync(item => item.Id == "push-1")).Enabled);
        Assert.True((await context.PushSubscriptions.SingleAsync(item => item.Id == "push-2")).Enabled);
        Assert.True((await context.FinancialSettings.SingleAsync()).CategoryLimitAlertsEnabled);
    }

    [Fact]
    public async Task ProcessPendingAsync_IgnoresHistoricalAndTransferRows()
    {
        var dbName = NewDbName();
        await SeedAsync(dbName, ("Dining", 100m, 79m));
        await using var context = NewContext(dbName, authenticated: true);
        var sender = new FakeSender();

        var historical = NewExpense("tx-old", "Dining", 30m);
        historical.Date = new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc);
        context.Transactions.Add(historical);
        context.Transactions.Add(new Transaction
        {
            Id = "tx-transfer",
            Date = CurrentDate,
            Description = "Transfer",
            Category = "Transfer",
            LedgerCategory = "Transfer:Rewards->Essentials",
            Amount = -30m
        });
        await context.SaveChangesAsync();
        await NewProcessor(context, sender).ProcessPendingAsync();

        Assert.Empty(sender.Sent);
    }

    private static readonly DateTime CurrentDate = new(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);

    private static string NewDbName() => $"category-limit-alert-{Guid.NewGuid():N}";

    private static AppDbContext NewContext(string dbName, bool authenticated)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var context = new AppDbContext(options);
        if (authenticated) context.SetCurrentUser("user-a");
        return context;
    }

    private static async Task SeedAsync(
        string dbName,
        params (string Category, decimal Limit, decimal Spent)[] categories) =>
        await SeedAsync(dbName, categories, categoryAlertsEnabled: true);

    private static async Task SeedAsync(
        string dbName,
        (string Category, decimal Limit, decimal Spent)[] categories,
        bool categoryAlertsEnabled)
    {
        await using var context = NewContext(dbName, authenticated: false);
        context.FinancialSettings.Add(new FinancialSetting
        {
            UserId = "user-a",
            CycleDay = 1,
            CategoryLimitAlertsEnabled = categoryAlertsEnabled
        });
        context.PushSubscriptions.Add(new PushSubscription
        {
            Id = "push-1",
            UserId = "user-a",
            DeviceId = "device-1",
            FcmToken = "token-1",
            Enabled = true,
            // Spending alerts are opted into per device now, so the device flag has to agree with
            // the account mirror or nothing is deliverable.
            CategoryAlertsEnabled = categoryAlertsEnabled,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        foreach (var (category, limit, spent) in categories)
        {
            context.TransactionCategories.Add(new TransactionCategory
            {
                Id = $"cat-{category}",
                UserId = "user-a",
                Name = category,
                Type = CategoryFlowType.Outflow,
                CycleLimit = limit
            });
            context.CategorySpendingGuides.Add(new CategorySpendingGuide
            {
                Id = $"guide-{category}",
                UserId = "user-a",
                CategoryName = category,
                EffectiveFromCycleKey = "2026-08",
                LimitAmount = limit
            });
            context.Transactions.Add(new Transaction
            {
                Id = $"seed-{category}",
                UserId = "user-a",
                Date = CurrentDate,
                Description = "Existing spend",
                Category = category,
                LedgerCategory = "Rewards",
                Amount = -spent
            });
        }
        await context.SaveChangesAsync();
    }

    private static async Task AddExpenseAsync(AppDbContext context, string id, string category, decimal amount)
    {
        context.Transactions.Add(NewExpense(id, category, amount));
        await context.SaveChangesAsync();
    }

    private static Transaction NewExpense(string id, string category, decimal amount) => new()
    {
        Id = id,
        Date = CurrentDate,
        Description = "Expense",
        Category = category,
        LedgerCategory = "Rewards",
        Amount = -amount
    };

    private static CategoryLimitAlertProcessor NewProcessor(AppDbContext context, IFcmPushSender sender)
    {
        var configuration = TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC"));
        var clock = new FinancialClock(
            configuration,
            new FixedTimeProvider(new DateTimeOffset(CurrentDate.AddHours(1))));
        return new CategoryLimitAlertProcessor(
            context,
            sender,
            new PushSubscriptionService(context, new FixedTimeProvider(CurrentDate.AddHours(1))),
            clock,
            NullLogger<CategoryLimitAlertProcessor>.Instance);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeSender : IFcmPushSender
    {
        public List<(string Token, PushNotificationContent Content)> Sent { get; } = [];
        public FcmSendResult Result { get; set; } = new(FcmSendStatus.Sent);
        public Func<string, FcmSendResult>? ResultForToken { get; set; }

        public Task<FcmSendResult> SendAsync(
            string fcmToken,
            PushNotificationContent content,
            CancellationToken cancellationToken = default)
        {
            Sent.Add((fcmToken, content));
            return Task.FromResult(ResultForToken?.Invoke(fcmToken) ?? Result);
        }
    }
}
