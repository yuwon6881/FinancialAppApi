using FinancialAppApi.Controllers;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Push;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class PushControllerTests
{
    [Fact]
    public async Task PutSubscription_RenewedCategoryTokenDeliversWaitingAlertImmediately()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"push-controller-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        context.SetCurrentUser("user-a");
        var now = DateTime.UtcNow;
        context.FinancialSettings.Add(new FinancialSetting
        {
            UserId = "user-a",
            CycleDay = 1,
            CategoryLimitAlertsEnabled = false
        });
        context.PushSubscriptions.Add(new PushSubscription
        {
            Id = "push-1",
            UserId = "user-a",
            DeviceId = "device-1",
            FcmToken = string.Empty,
            Enabled = false,
            CategoryAlertsEnabled = false,
            CreatedAt = now,
            UpdatedAt = now
        });
        context.CategoryLimitAlertEvents.Add(new CategoryLimitAlertEvent
        {
            Id = "event-1",
            UserId = "user-a",
            CycleKey = $"{now:yyyy-MM}",
            Title = "Category spending alert",
            Body = "Hobbies has reached what you planned to spend on it this cycle.",
            Tag = $"category-limit-{now:yyyy-MM}",
            CategoryName = "Hobbies",
            CreatedAt = now,
            ExpiresAt = now.AddHours(1),
            AwaitingDeviceRecovery = true
        });
        context.CategoryLimitAlertMilestones.Add(new CategoryLimitAlertMilestone
        {
            Id = "milestone-1",
            UserId = "user-a",
            EventId = "event-1",
            CycleKey = $"{now:yyyy-MM}",
            CategoryName = "Hobbies",
            Milestone = "Limit"
        });
        await context.SaveChangesAsync();

        var timeProvider = new FixedTimeProvider(new DateTimeOffset(now));
        var subscriptionService = new PushSubscriptionService(context, timeProvider);
        var sender = new FakeSender();
        var clock = new FinancialClock(
            TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")),
            timeProvider);
        var processor = new CategoryLimitAlertProcessor(
            context,
            sender,
            subscriptionService,
            clock,
            NullLogger<CategoryLimitAlertProcessor>.Instance);
        var services = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new PushDispatchService(
            services.GetRequiredService<IServiceScopeFactory>(),
            TestHelpers.NewConfiguration(("Fcm:ProjectId", "test-project")),
            NullLogger<PushDispatchService>.Instance);
        var controller = new PushController(subscriptionService, dispatcher, processor)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.PutSubscription(new PushSubscribeDto
        {
            DeviceId = "device-1",
            FcmToken = "token-renewed",
            BillReminders = false,
            CategoryAlerts = true
        });

        Assert.IsType<OkResult>(result);
        Assert.Equal("token-renewed", Assert.Single(sender.SentTokens));
        var alertEvent = await context.CategoryLimitAlertEvents.SingleAsync();
        Assert.False(alertEvent.AwaitingDeviceRecovery);
        Assert.NotNull(alertEvent.CompletedAt);
    }

    [Fact]
    public async Task PutSubscription_TokenRefreshWithoutChannelFlags_StillDeliversAWaitingAlert()
    {
        // What a launch actually sends: an already-enrolled device re-registering its rotated
        // token, with both channel flags omitted so it cannot rewrite the user's choices. This is
        // the most frequent opportunity to deliver an alert whose inline attempt was starved.
        var harness = await NewHarnessAsync(deviceCategoryAlerts: true, deviceEnabled: true);

        var result = await harness.Controller.PutSubscription(new PushSubscribeDto
        {
            DeviceId = "device-1",
            FcmToken = "token-rotated"
        });

        Assert.IsType<OkResult>(result);
        Assert.Equal("token-rotated", Assert.Single(harness.Sender.SentTokens));
        Assert.NotNull((await harness.Context.CategoryLimitAlertEvents.SingleAsync()).CompletedAt);
    }

    [Fact]
    public async Task PutSubscription_TokenRefreshForABillsOnlyDevice_DoesNotDeliverSpendingAlerts()
    {
        var harness = await NewHarnessAsync(deviceCategoryAlerts: false, deviceEnabled: true);

        var result = await harness.Controller.PutSubscription(new PushSubscribeDto
        {
            DeviceId = "device-1",
            FcmToken = "token-rotated"
        });

        Assert.IsType<OkResult>(result);
        Assert.Empty(harness.Sender.SentTokens);
    }

    private sealed record Harness(AppDbContext Context, PushController Controller, FakeSender Sender);

    private static async Task<Harness> NewHarnessAsync(bool deviceCategoryAlerts, bool deviceEnabled)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"push-controller-{Guid.NewGuid():N}")
            .Options;
        var context = new AppDbContext(options);
        context.SetCurrentUser("user-a");
        var now = DateTime.UtcNow;
        context.FinancialSettings.Add(new FinancialSetting
        {
            UserId = "user-a",
            CycleDay = 1,
            CategoryLimitAlertsEnabled = deviceCategoryAlerts
        });
        context.PushSubscriptions.Add(new PushSubscription
        {
            Id = "push-1",
            UserId = "user-a",
            DeviceId = "device-1",
            FcmToken = "token-old",
            Enabled = deviceEnabled,
            BillRemindersEnabled = true,
            CategoryAlertsEnabled = deviceCategoryAlerts,
            CreatedAt = now,
            UpdatedAt = now
        });
        context.CategoryLimitAlertEvents.Add(new CategoryLimitAlertEvent
        {
            Id = "event-1",
            UserId = "user-a",
            CycleKey = $"{now:yyyy-MM}",
            Title = "Category spending alert",
            Body = "Hobbies has reached what you planned to spend on it this cycle.",
            Tag = $"category-limit-{now:yyyy-MM}",
            CategoryName = "Hobbies",
            CreatedAt = now,
            ExpiresAt = now.AddHours(1)
        });
        context.CategoryLimitAlertMilestones.Add(new CategoryLimitAlertMilestone
        {
            Id = "milestone-1",
            UserId = "user-a",
            EventId = "event-1",
            CycleKey = $"{now:yyyy-MM}",
            CategoryName = "Hobbies",
            Milestone = "Limit"
        });
        await context.SaveChangesAsync();

        var timeProvider = new FixedTimeProvider(new DateTimeOffset(now));
        var subscriptionService = new PushSubscriptionService(context, timeProvider);
        var sender = new FakeSender();
        var processor = new CategoryLimitAlertProcessor(
            context,
            sender,
            subscriptionService,
            new FinancialClock(TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")), timeProvider),
            NullLogger<CategoryLimitAlertProcessor>.Instance);
        var services = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new PushDispatchService(
            services.GetRequiredService<IServiceScopeFactory>(),
            TestHelpers.NewConfiguration(("Fcm:ProjectId", "test-project")),
            NullLogger<PushDispatchService>.Instance);
        var controller = new PushController(subscriptionService, dispatcher, processor)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return new Harness(context, controller, sender);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeSender : IFcmPushSender
    {
        public List<string> SentTokens { get; } = [];

        public Task<FcmSendResult> SendAsync(
            string fcmToken,
            PushNotificationContent content,
            CancellationToken cancellationToken = default)
        {
            SentTokens.Add(fcmToken);
            return Task.FromResult(new FcmSendResult(FcmSendStatus.Sent));
        }
    }
}
