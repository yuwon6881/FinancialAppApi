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
        Assert.False(status.CategoryAlertsEnabled);
    }

    [Fact]
    public async Task GetStatusAsync_ReflectsAccountToggleAndDeviceSubscription()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var setting = NewSetting();
        setting.CategoryLimitAlertsEnabled = true;
        context.FinancialSettings.Add(setting);
        context.PushSubscriptions.Add(NewSubscription("device-1"));
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var status = await service.GetStatusAsync("device-1");

        Assert.True(status.AccountEnabled);
        Assert.True(status.DeviceSubscribed);
        Assert.True(status.CategoryAlertsEnabled);
    }

    [Fact]
    public async Task SetCategoryAlertsEnabledAsync_RequiresAnEnabledPushDevice()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var updated = await service.SetCategoryAlertsEnabledAsync(true);

        Assert.False(updated);
        Assert.False((await context.FinancialSettings.SingleAsync()).CategoryLimitAlertsEnabled);
    }

    [Fact]
    public async Task SetCategoryAlertsEnabledAsync_PersistsExplicitConsent()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        context.PushSubscriptions.Add(NewSubscription("device-1"));
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var updated = await service.SetCategoryAlertsEnabledAsync(true);

        Assert.True(updated);
        Assert.True((await context.FinancialSettings.SingleAsync()).CategoryLimitAlertsEnabled);
    }

    [Fact]
    public async Task GetStatusAsync_DeviceSubscribedFalseForDisabledSubscription()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        var subscription = NewSubscription("device-1");
        subscription.Enabled = false;
        context.PushSubscriptions.Add(subscription);
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var status = await service.GetStatusAsync("device-1");

        Assert.False(status.DeviceSubscribed);
    }

    [Fact]
    public async Task SubscribeAsync_CreatesNewSubscription()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var subscription = await service.SubscribeAsync("device-1", "token-abc");

        Assert.Equal("device-1", subscription.DeviceId);
        Assert.Equal("token-abc", subscription.FcmToken);
        Assert.True(subscription.Enabled);
        Assert.True((await service.GetStatusAsync("device-1")).AccountEnabled);
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
    public async Task UnsubscribeAsync_DisablesTheRowAndKeepsItsIdForDeliveryClaims()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        context.PushSubscriptions.Add(NewSubscription("device-1"));
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var removed = await service.UnsubscribeAsync("device-1");

        Assert.True(removed);
        var row = await context.PushSubscriptions.SingleAsync();
        Assert.False(row.Enabled);
        Assert.Equal(string.Empty, row.FcmToken);
        Assert.Equal("push-device-1", row.Id);
    }

    [Fact]
    public async Task SubscribeAsync_AfterUnsubscribe_KeepsTheSameSubscriptionId()
    {
        // Delivery claims (PushReminderDelivery/CategoryLimitAlertDelivery) are keyed on this id.
        // A new id here would orphan every claim and re-send a reminder already delivered today.
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        context.PushSubscriptions.Add(NewSubscription("device-1"));
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        await service.UnsubscribeAsync("device-1");
        var resubscribed = await service.SubscribeAsync("device-1", "token-fresh");

        Assert.Equal("push-device-1", resubscribed.Id);
        Assert.True(resubscribed.Enabled);
        Assert.Equal("token-fresh", resubscribed.FcmToken);
        Assert.Single(await context.PushSubscriptions.ToListAsync());
    }

    [Fact]
    public async Task UnsubscribeAsync_ClearsCategoryAlertConsentWhenTheLastDeviceLeaves()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var setting = NewSetting();
        setting.CategoryLimitAlertsEnabled = true;
        context.FinancialSettings.Add(setting);
        context.PushSubscriptions.Add(NewSubscription("device-1"));
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        await service.UnsubscribeAsync("device-1");

        Assert.False((await context.FinancialSettings.FirstAsync()).CategoryLimitAlertsEnabled);
    }

    [Fact]
    public async Task UnsubscribeAsync_KeepsCategoryAlertConsentWhenAnotherDeviceRemains()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var setting = NewSetting();
        setting.CategoryLimitAlertsEnabled = true;
        context.FinancialSettings.Add(setting);
        context.PushSubscriptions.AddRange(NewSubscription("device-1"), NewSubscription("device-2"));
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        await service.UnsubscribeAsync("device-1");

        Assert.True((await context.FinancialSettings.FirstAsync()).CategoryLimitAlertsEnabled);
        var status = await service.GetStatusAsync("device-2");
        Assert.True(status.AccountEnabled);
        Assert.True(status.DeviceSubscribed);
    }

    [Fact]
    public async Task GetDevicesAsync_ListsEnabledDevicesAndMarksTheCurrentOne()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        var stale = NewSubscription("device-old");
        stale.Enabled = false;
        context.PushSubscriptions.AddRange(NewSubscription("device-1"), NewSubscription("device-2"), stale);
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var devices = await service.GetDevicesAsync("device-2");

        Assert.Equal(2, devices.Count);
        Assert.Single(devices, device => device.IsCurrent);
        Assert.Contains(devices, device => device.Id == "push-device-2" && device.IsCurrent);
    }

    [Fact]
    public async Task RevokeDeviceAsync_DisablesAnotherDeviceById()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        context.PushSubscriptions.AddRange(NewSubscription("device-1"), NewSubscription("device-2"));
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var revoked = await service.RevokeDeviceAsync("push-device-1");

        Assert.True(revoked);
        Assert.False(await service.RevokeDeviceAsync("push-missing"));
        var devices = await service.GetDevicesAsync("device-2");
        Assert.Equal("push-device-2", Assert.Single(devices).Id);
    }

    [Fact]
    public async Task GetStatusAsync_DoesNotReportEnabledElsewhereWithoutALiveSubscription()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        var disabled = NewSubscription("stale-device");
        disabled.Enabled = false;
        context.PushSubscriptions.Add(disabled);
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var status = await service.GetStatusAsync("new-device");

        Assert.False(status.AccountEnabled);
        Assert.False(status.DeviceSubscribed);
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

    private static FinancialSetting NewSetting()
    {
        return new FinancialSetting
        {
            CycleDay = 1
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
