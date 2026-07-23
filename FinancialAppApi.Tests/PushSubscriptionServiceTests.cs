using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests;

public class PushSubscriptionServiceTests
{
    [Fact]
    public async Task GetStatusAsync_DefaultsToDisabledWhenNoSettingRow()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = new PushSubscriptionService(context);

        var status = await service.GetStatusAsync(null);

        Assert.False(status.AccountEnabled);
        Assert.False(status.DeviceSubscribed);
    }

    [Fact]
    public async Task GetStatusAsync_ReflectsAccountToggleAndDeviceSubscription()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting(pushEnabled: true));
        context.PushSubscriptions.Add(NewSubscription("device-1"));
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var status = await service.GetStatusAsync("device-1");

        Assert.True(status.AccountEnabled);
        Assert.True(status.DeviceSubscribed);
    }

    [Fact]
    public async Task GetStatusAsync_DeviceSubscribedFalseForDisabledSubscription()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting(pushEnabled: true));
        var subscription = NewSubscription("device-1");
        subscription.Enabled = false;
        context.PushSubscriptions.Add(subscription);
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var status = await service.GetStatusAsync("device-1");

        Assert.False(status.DeviceSubscribed);
    }

    [Fact]
    public async Task SetAccountEnabledAsync_UpdatesExistingSetting()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting(pushEnabled: false));
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var updated = await service.SetAccountEnabledAsync(true);

        Assert.True(updated);
        Assert.True((await context.FinancialSettings.FirstAsync()).PushRemindersEnabled);
    }

    [Fact]
    public async Task SetAccountEnabledAsync_ReturnsFalseWhenNoSettingRow()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = new PushSubscriptionService(context);

        var updated = await service.SetAccountEnabledAsync(true);

        Assert.False(updated);
    }

    [Fact]
    public async Task SubscribeAsync_CreatesNewSubscription()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = new PushSubscriptionService(context);

        var subscription = await service.SubscribeAsync("device-1", "token-abc");

        Assert.Equal("device-1", subscription.DeviceId);
        Assert.Equal("token-abc", subscription.FcmToken);
        Assert.True(subscription.Enabled);
        Assert.Equal(1, await context.PushSubscriptions.CountAsync());
    }

    [Fact]
    public async Task SubscribeAsync_UpsertsTokenForExistingDeviceInstead_OfDuplicating()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = new PushSubscriptionService(context);
        await service.SubscribeAsync("device-1", "token-old");

        var updated = await service.SubscribeAsync("device-1", "token-new");

        Assert.Equal(1, await context.PushSubscriptions.CountAsync());
        Assert.Equal("token-new", updated.FcmToken);
    }

    [Fact]
    public async Task SubscribeAsync_ReEnablesAPreviouslyDisabledSubscription()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var subscription = NewSubscription("device-1");
        subscription.Enabled = false;
        context.PushSubscriptions.Add(subscription);
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var result = await service.SubscribeAsync("device-1", "token-new");

        Assert.True(result.Enabled);
    }

    [Fact]
    public async Task UnsubscribeAsync_RemovesExistingSubscription()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.PushSubscriptions.Add(NewSubscription("device-1"));
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var removed = await service.UnsubscribeAsync("device-1");

        Assert.True(removed);
        Assert.Empty(await context.PushSubscriptions.ToListAsync());
    }

    [Fact]
    public async Task UnsubscribeAsync_ReturnsFalseWhenDeviceUnknown()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = new PushSubscriptionService(context);

        var removed = await service.UnsubscribeAsync("missing-device");

        Assert.False(removed);
    }

    [Fact]
    public async Task Subscriptions_AreIsolatedPerUser()
    {
        await using var context = TestHelpers.NewInMemoryContext(currentUserId: "user-a");
        context.PushSubscriptions.Add(NewSubscription("device-1"));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        context.SetCurrentUser("user-b");
        var serviceB = new PushSubscriptionService(context);

        var status = await serviceB.GetStatusAsync("device-1");

        Assert.False(status.DeviceSubscribed);
    }

    private static FinancialSetting NewSetting(bool pushEnabled)
    {
        return new FinancialSetting
        {
            CycleDay = 1,
            PushRemindersEnabled = pushEnabled
        };
    }

    private static PushSubscription NewSubscription(string deviceId)
    {
        return new PushSubscription
        {
            Id = $"push-{deviceId}",
            DeviceId = deviceId,
            FcmToken = "token",
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }
}
