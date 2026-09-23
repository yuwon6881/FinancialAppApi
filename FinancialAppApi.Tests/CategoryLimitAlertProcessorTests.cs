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
    public async Task ProcessPendingAsync_SendsCategoryDetailsInTheNotificationPreview()
    {
        var dbName = NewDbName();
        await SeedAsync(dbName, ("Dining", 100m, 79m));
        await using var context = NewContext(dbName, authenticated: true);
        var sender = new FakeSender();

        await AddExpenseAsync(context, "tx-near-private", "Dining", 1m);
        await NewProcessor(context, sender).ProcessPendingAsync();

        var content = Assert.Single(sender.Sent).Content;
        Assert.Equal("Category spending alert", content.Title);
        Assert.Contains("Dining", content.Body);
        Assert.Equal("Dining", content.Data["categoryName"]);
    }

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

    [Fact]
    public async Task ProcessPendingAsync_UndeliveredEvent_IsStillDeliverableByTheNextMorningScheduledRun()
    {
        // The inline attempt runs from Response.OnCompleted and can be starved or interrupted; the
        // only other caller is the 09:00 local scheduled dispatch. An event that expired at the end
        // of its own local day was always already expired by the time that run could see it, so a
        // crossing the inline attempt missed was lost for good -- a crossing needs
        // before < limit && after >= limit and therefore never fires twice.
        var dbName = NewDbName();
        await SeedAsync(dbName, ("Dining", 100m, 79m));
        await using var context = NewContext(dbName, authenticated: true);

        var failing = new FakeSender { Result = new FcmSendResult(FcmSendStatus.TransientFailure) };
        await AddExpenseAsync(context, "tx-limit", "Dining", 25m);
        var firstSummary = await NewProcessorAt(context, failing, CurrentDate.AddHours(1)).ProcessPendingAsync();

        Assert.Equal(0, firstSummary.Sent);
        Assert.Equal(1, firstSummary.Failed);
        var pending = await context.CategoryLimitAlertEvents.SingleAsync();
        Assert.Null(pending.CompletedAt);
        // End of the *next* local day: far short of this cycle's end, so the clamp does not bite.
        Assert.Equal(new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc), pending.ExpiresAt);

        var recovering = new FakeSender();
        var nextMorning = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);
        var secondSummary = await NewProcessorAt(context, recovering, nextMorning).ProcessPendingAsync();

        Assert.Equal(1, secondSummary.Sent);
        Assert.Equal("Dining has gone past what you planned to spend on it this cycle.",
            Assert.Single(recovering.Sent).Content.Body);
        Assert.NotNull((await context.CategoryLimitAlertEvents.SingleAsync()).CompletedAt);
        // Milestones stay claimed, so the recovered alert is not followed by a duplicate.
        Assert.Single(await context.CategoryLimitAlertMilestones.ToListAsync());
    }

    [Fact]
    public async Task ProcessPendingAsync_PushTtlNeverOutlivesTheDayItIsSentOn()
    {
        // The event stays deliverable into the next morning so a missed inline attempt can be
        // recovered; the push itself must not, because FCM holding a delivery past the day it was
        // sent on would surface a figure that has already moved on. The two bounds are different
        // numbers here on purpose: 47h of event validity, 23h of push TTL.
        var dbName = NewDbName();
        await SeedAsync(dbName, ("Dining", 100m, 79m));
        await using var context = NewContext(dbName, authenticated: true);

        var sender = new FakeSender();
        await AddExpenseAsync(context, "tx-limit", "Dining", 25m);
        await NewProcessorAt(context, sender, CurrentDate.AddHours(1)).ProcessPendingAsync();

        var alertEvent = await context.CategoryLimitAlertEvents.SingleAsync();
        Assert.Equal(TimeSpan.FromHours(47), alertEvent.ExpiresAt - CurrentDate.AddHours(1));
        Assert.Equal(TimeSpan.FromHours(23), Assert.Single(sender.Sent).Content.TimeToLive);
    }

    [Fact]
    public async Task ProcessPendingAsync_EventIsDroppedOnceItsRecoveryWindowHasPassed()
    {
        var dbName = NewDbName();
        await SeedAsync(dbName, ("Dining", 100m, 79m));
        await using var context = NewContext(dbName, authenticated: true);

        await AddExpenseAsync(context, "tx-limit", "Dining", 25m);
        await NewProcessorAt(context, new FakeSender { Result = new FcmSendResult(FcmSendStatus.TransientFailure) },
            CurrentDate.AddHours(1)).ProcessPendingAsync();

        var tooLate = new FakeSender();
        var secondMorning = new DateTime(2026, 8, 11, 9, 0, 0, DateTimeKind.Utc);
        var summary = await NewProcessorAt(context, tooLate, secondMorning).ProcessPendingAsync();

        Assert.Equal(0, summary.Sent);
        Assert.Empty(tooLate.Sent);
        Assert.NotNull((await context.CategoryLimitAlertEvents.SingleAsync()).CompletedAt);
        // The latch is released rather than left spent on an alert nobody ever received.
        Assert.Empty(await context.CategoryLimitAlertMilestones.ToListAsync());
    }

    [Fact]
    public async Task ProcessPendingAsync_RecoveryWindowIsClampedToTheEndOfItsOwnCycle()
    {
        // Cycle day 10 makes 2026-08-09 the final day of cycle 2026-07. Carrying the event into
        // 2026-08-11 would let it describe "this cycle" after the cycle had rolled over.
        var dbName = NewDbName();
        await SeedAsync(dbName, [("Dining", 100m, 79m)], categoryAlertsEnabled: true, cycleDay: 10, guideCycleKey: "2026-07");
        await using var context = NewContext(dbName, authenticated: true);

        await AddExpenseAsync(context, "tx-limit", "Dining", 25m);
        await NewProcessorAt(context, new FakeSender { Result = new FcmSendResult(FcmSendStatus.TransientFailure) },
            CurrentDate.AddHours(1)).ProcessPendingAsync();

        var alertEvent = await context.CategoryLimitAlertEvents.SingleAsync();
        Assert.Equal("2026-07", alertEvent.CycleKey);
        Assert.Equal(new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc), alertEvent.ExpiresAt);
    }

    // The alert arithmetic and the Reports screen have to count the same rows, or the guidance
    // contradicts the figure the user is looking at. A discarded recurring-payment marker is not
    // spending -- reporting excludes it -- so it must not inflate the category total either. It
    // did, and the effect was worse than a wrong number: with the marker counted, "before" was
    // already at the limit, so the crossing test (before < limit && after >= limit) never fired
    // and the category that visibly reached its guide raised nothing at all.
    [Fact]
    public async Task ProcessPendingAsync_IgnoresDiscardedMarkersWhenTotallingCategorySpend()
    {
        var dbName = NewDbName();
        await SeedAsync(dbName, ("Dining", 100m, 40m));
        await using var context = NewContext(dbName, authenticated: true);
        var sender = new FakeSender();

        context.Transactions.Add(new Transaction
        {
            Id = "tx-discarded",
            Date = CurrentDate,
            Description = "Discarded bill marker",
            Category = "Dining",
            LedgerCategory = "Discarded",
            Amount = -60m
        });
        await context.SaveChangesAsync();
        await NewProcessor(context, sender).ProcessPendingAsync();

        // 40 + 60 would have passed the 100 limit on the marker alone.
        Assert.Empty(sender.Sent);
        Assert.Empty(await context.CategoryLimitAlertMilestones.ToListAsync());

        await AddExpenseAsync(context, "tx-real", "Dining", 61m);
        await NewProcessor(context, sender).ProcessPendingAsync();

        // Real spend of 40 + 61 crosses 100, and the marker must not have consumed the milestone.
        var content = Assert.Single(sender.Sent).Content;
        Assert.Equal("Dining has gone past what you planned to spend on it this cycle.", content.Body);
    }

    // AccountMove is an internal move between accounts inside one bucket and has no bucket effect,
    // so its negative leg is not spending on the category it carries.
    [Fact]
    public async Task ProcessPendingAsync_IgnoresAccountMoveLegsWhenTotallingCategorySpend()
    {
        var dbName = NewDbName();
        await SeedAsync(dbName, ("Dining", 100m, 40m));
        await using var context = NewContext(dbName, authenticated: true);
        var sender = new FakeSender();

        context.Transactions.Add(new Transaction
        {
            Id = "tx-move",
            Date = CurrentDate,
            Description = "Internal move",
            Category = "Dining",
            LedgerCategory = "AccountMove",
            Amount = -70m
        });
        await context.SaveChangesAsync();
        await NewProcessor(context, sender).ProcessPendingAsync();

        Assert.Empty(sender.Sent);
        Assert.Empty(await context.CategoryLimitAlertMilestones.ToListAsync());
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
        bool categoryAlertsEnabled,
        int cycleDay = 1,
        string guideCycleKey = "2026-08")
    {
        await using var context = NewContext(dbName, authenticated: false);
        context.FinancialSettings.Add(new FinancialSetting
        {
            UserId = "user-a",
            CycleDay = cycleDay,
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
                EffectiveFromCycleKey = guideCycleKey,
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

    private static CategoryLimitAlertProcessor NewProcessor(AppDbContext context, IFcmPushSender sender) =>
        NewProcessorAt(context, sender, CurrentDate.AddHours(1));

    private static CategoryLimitAlertProcessor NewProcessorAt(AppDbContext context, IFcmPushSender sender, DateTime nowUtc)
    {
        var configuration = TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC"));
        var clock = new FinancialClock(
            configuration,
            new FixedTimeProvider(new DateTimeOffset(nowUtc)));
        return new CategoryLimitAlertProcessor(
            context,
            sender,
            new PushSubscriptionService(context, new FixedTimeProvider(nowUtc)),
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
