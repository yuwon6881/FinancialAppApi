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
        Assert.Contains(sender.Sent, item => item.Content.Body == "Dining is close to its cycle spending guide.");
        Assert.Contains(sender.Sent, item => item.Content.Body == "Dining has reached its cycle spending guide.");
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
        Assert.Equal("Dining has gone over its cycle spending guide.", content.Body);
        var milestone = Assert.Single(await context.CategoryLimitAlertMilestones.ToListAsync());
        Assert.Equal("Limit", milestone.Milestone);
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
        Assert.Equal("2 categories crossed a cycle spending milestone.", content.Body);
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
            CategoryLimitAlertsEnabled = categoryAlertsEnabled,
            PushRemindersEnabled = true
        });
        context.PushSubscriptions.Add(new PushSubscription
        {
            Id = "push-1",
            UserId = "user-a",
            DeviceId = "device-1",
            FcmToken = "token-1",
            Enabled = true,
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

        public Task<FcmSendResult> SendAsync(
            string fcmToken,
            PushNotificationContent content,
            CancellationToken cancellationToken = default)
        {
            Sent.Add((fcmToken, content));
            return Task.FromResult(new FcmSendResult(FcmSendStatus.Sent));
        }
    }
}
