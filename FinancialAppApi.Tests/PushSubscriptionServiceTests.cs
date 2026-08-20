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
        Assert.False(status.ThisDeviceBillReminders);
        Assert.False(status.ThisDeviceCategoryAlerts);
        Assert.False(status.TokenRenewalRequired);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsEachChannelForThisDevice()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        context.PushSubscriptions.Add(NewSubscription("device-1", billReminders: true, categoryAlerts: true));
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var status = await service.GetStatusAsync("device-1");

        Assert.True(status.AccountEnabled);
        Assert.True(status.DeviceSubscribed);
        Assert.True(status.ThisDeviceBillReminders);
        Assert.True(status.ThisDeviceCategoryAlerts);
        Assert.False(status.OtherDevicesBillReminders);
        Assert.False(status.OtherDevicesCategoryAlerts);
    }

    [Fact]
    public async Task GetStatusAsync_NeverReportsAnotherDevicesOptInAsThisDevices()
    {
        // The whole point of the per-device columns: a phone opted into spending alerts must not
        // make the desktop's switch read "on", because the desktop will receive nothing.
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        context.PushSubscriptions.Add(NewSubscription("phone", billReminders: true, categoryAlerts: true));
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var status = await service.GetStatusAsync("desktop");

        Assert.False(status.ThisDeviceBillReminders);
        Assert.False(status.ThisDeviceCategoryAlerts);
        Assert.False(status.DeviceSubscribed);
        Assert.True(status.OtherDevicesBillReminders);
        Assert.True(status.OtherDevicesCategoryAlerts);
        Assert.True(status.AccountEnabled);
    }

    [Fact]
    public async Task GetStatusAsync_DeviceSubscribedFalseForDisabledSubscription()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        var subscription = NewSubscription("device-1");
        subscription.Enabled = false;
        subscription.BillRemindersEnabled = false;
        context.PushSubscriptions.Add(subscription);
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var status = await service.GetStatusAsync("device-1");

        Assert.False(status.DeviceSubscribed);
    }

    [Fact]
    public async Task GetStatusAsync_RequiresRenewalOnlyForThisDevicesRetiredToken()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        var retired = NewSubscription("device-1");
        retired.Enabled = false;
        retired.BillRemindersEnabled = false;
        retired.CategoryAlertsEnabled = false;
        retired.FcmToken = string.Empty;
        context.PushSubscriptions.Add(retired);
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var retiredStatus = await service.GetStatusAsync("device-1");
        var newDeviceStatus = await service.GetStatusAsync("new-device");

        Assert.True(retiredStatus.TokenRenewalRequired);
        Assert.False(newDeviceStatus.TokenRenewalRequired);
    }

    [Fact]
    public async Task SubscribeAsync_NewDeviceTakesBillRemindersOnlyByDefault()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var subscription = await service.SubscribeAsync("device-1", "token-abc");

        Assert.NotNull(subscription);
        Assert.Equal("device-1", subscription!.DeviceId);
        Assert.Equal("token-abc", subscription.FcmToken);
        Assert.True(subscription.Enabled);
        Assert.True(subscription.BillRemindersEnabled);
        Assert.False(subscription.CategoryAlertsEnabled);
        Assert.Equal(1, await context.PushSubscriptions.CountAsync());
    }

    [Fact]
    public async Task SubscribeAsync_TurningOnOneChannelLeavesTheOtherAlone()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        await service.SubscribeAsync("device-1", "token", billReminders: false, categoryAlerts: true);
        var afterBills = await service.SubscribeAsync("device-1", "token", billReminders: true);

        Assert.NotNull(afterBills);
        Assert.True(afterBills!.BillRemindersEnabled);
        Assert.True(afterBills.CategoryAlertsEnabled);
    }

    [Fact]
    public async Task SubscribeAsync_CategoryAlertsAloneKeepsBillRemindersOff()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var subscription = await service.SubscribeAsync(
            "device-1", "token", billReminders: false, categoryAlerts: true);

        Assert.NotNull(subscription);
        Assert.False(subscription!.BillRemindersEnabled);
        Assert.True(subscription.CategoryAlertsEnabled);
        Assert.True(subscription.Enabled);
    }

    [Fact]
    public async Task SubscribeAsync_UpsertsTokenForExistingDeviceInstead_OfDuplicating()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = new PushSubscriptionService(context);
        await service.SubscribeAsync("device-1", "token-old");

        var updated = await service.SubscribeAsync("device-1", "token-new");

        Assert.Equal(1, await context.PushSubscriptions.CountAsync());
        Assert.Equal("token-new", updated!.FcmToken);
    }

    [Fact]
    public async Task SubscribeAsync_ReEnablesAPreviouslyDisabledSubscription()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var subscription = NewSubscription("device-1");
        subscription.Enabled = false;
        subscription.BillRemindersEnabled = false;
        context.PushSubscriptions.Add(subscription);
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var result = await service.SubscribeAsync("device-1", "token-new", billReminders: true);

        Assert.NotNull(result);
        Assert.True(result!.Enabled);
        Assert.True(result.BillRemindersEnabled);
    }

    [Fact]
    public async Task SubscribeAsync_WithBothChannelsOffUnsubscribesTheDevice()
    {
        // The two switches must not be able to leave a registered device that receives nothing:
        // the dispatcher would keep it in the fan-out forever.
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);
        await service.SubscribeAsync("device-1", "token", billReminders: true, categoryAlerts: true);

        var result = await service.SubscribeAsync(
            "device-1", "token", billReminders: false, categoryAlerts: false);

        Assert.Null(result);
        var row = await context.PushSubscriptions.SingleAsync();
        Assert.False(row.Enabled);
        Assert.Equal(string.Empty, row.FcmToken);
    }

    [Fact]
    public async Task DisableChannelAsync_TurnsOffOneKindAndKeepsTheOther()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        context.PushSubscriptions.Add(NewSubscription("device-1", billReminders: true, categoryAlerts: true));
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var changed = await service.DisableChannelAsync("device-1", PushChannel.CategoryAlerts);

        Assert.True(changed);
        var row = await context.PushSubscriptions.SingleAsync();
        Assert.True(row.Enabled);
        Assert.True(row.BillRemindersEnabled);
        Assert.False(row.CategoryAlertsEnabled);
        Assert.NotEqual(string.Empty, row.FcmToken);
    }

    [Fact]
    public async Task DisableChannelAsync_TurningOffTheLastKindDisablesTheDevice()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        context.PushSubscriptions.Add(NewSubscription("device-1", billReminders: true));
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        await service.DisableChannelAsync("device-1", PushChannel.BillReminders);

        var row = await context.PushSubscriptions.SingleAsync();
        Assert.False(row.Enabled);
        Assert.Equal(string.Empty, row.FcmToken);
        Assert.Equal("push-device-1", row.Id);
    }

    [Fact]
    public async Task DisableChannelAsync_ReturnsFalseForAnUnknownDevice()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = new PushSubscriptionService(context);

        Assert.False(await service.DisableChannelAsync("missing", PushChannel.BillReminders));
    }

    [Fact]
    public async Task CategoryAlertConsent_MirrorsWhetherAnyDeviceWantsThem()
    {
        // FinancialSetting.CategoryLimitAlertsEnabled is a derived mirror of the device rows --
        // it exists only so the SaveChanges-time capture can skip accounts that want no alerts.
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        await service.SubscribeAsync("device-1", "token", categoryAlerts: true);
        Assert.True((await context.FinancialSettings.FirstAsync()).CategoryLimitAlertsEnabled);

        await service.DisableChannelAsync("device-1", PushChannel.CategoryAlerts);
        Assert.False((await context.FinancialSettings.FirstAsync()).CategoryLimitAlertsEnabled);
    }

    [Fact]
    public async Task CategoryAlertConsent_SurvivesOneOfTwoDevicesLeaving()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);
        await service.SubscribeAsync("device-1", "token", categoryAlerts: true);
        await service.SubscribeAsync("device-2", "token", categoryAlerts: true);

        await service.UnsubscribeAsync("device-1");

        Assert.True((await context.FinancialSettings.FirstAsync()).CategoryLimitAlertsEnabled);
        var status = await service.GetStatusAsync("device-2");
        Assert.True(status.ThisDeviceCategoryAlerts);
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
        Assert.False(row.BillRemindersEnabled);
        Assert.False(row.CategoryAlertsEnabled);
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
        var resubscribed = await service.SubscribeAsync("device-1", "token-fresh", billReminders: true);

        Assert.Equal("push-device-1", resubscribed!.Id);
        Assert.True(resubscribed.Enabled);
        Assert.Equal("token-fresh", resubscribed.FcmToken);
        Assert.Single(await context.PushSubscriptions.ToListAsync());
    }

    [Fact]
    public async Task GetDevicesAsync_ListsEnabledDevicesWithTheirChannels()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(NewSetting());
        var stale = NewSubscription("device-old");
        stale.Enabled = false;
        stale.BillRemindersEnabled = false;
        context.PushSubscriptions.AddRange(
            NewSubscription("device-1", billReminders: true),
            NewSubscription("device-2", billReminders: false, categoryAlerts: true),
            stale);
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var devices = await service.GetDevicesAsync("device-2");

        Assert.Equal(2, devices.Count);
        Assert.Single(devices, device => device.IsCurrent);
        var current = devices.Single(device => device.IsCurrent);
        Assert.Equal("push-device-2", current.Id);
        Assert.False(current.BillReminders);
        Assert.True(current.CategoryAlerts);
        var other = devices.Single(device => !device.IsCurrent);
        Assert.True(other.BillReminders);
        Assert.False(other.CategoryAlerts);
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
        disabled.BillRemindersEnabled = false;
        context.PushSubscriptions.Add(disabled);
        await context.SaveChangesAsync();
        var service = new PushSubscriptionService(context);

        var status = await service.GetStatusAsync("new-device");

        Assert.False(status.AccountEnabled);
        Assert.False(status.DeviceSubscribed);
        Assert.False(status.OtherDevicesBillReminders);
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

    private static PushSubscription NewSubscription(
        string deviceId,
        bool billReminders = true,
        bool categoryAlerts = false)
    {
        return new PushSubscription
        {
            Id = $"push-{deviceId}",
            DeviceId = deviceId,
            FcmToken = "token",
            Enabled = billReminders || categoryAlerts,
            BillRemindersEnabled = billReminders,
            CategoryAlertsEnabled = categoryAlerts,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }
}
