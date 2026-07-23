using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests;

public class DatabaseInvariantTests
{
    [Fact]
    public void Model_EnforcesOneClosedRegistrationSlot()
    {
        using var context = TestHelpers.NewInMemoryContext();
        var entity = context.Model.FindEntityType(typeof(AppUser))!;

        var index = Assert.Single(entity.GetIndexes(), i =>
            i.Properties.Select(p => p.Name).SequenceEqual([nameof(AppUser.RegistrationSlot)]));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void Model_EnforcesOneTransactionPerWishlistPurchase()
    {
        using var context = TestHelpers.NewInMemoryContext();
        var entity = context.Model.FindEntityType(typeof(Transaction))!;

        var index = Assert.Single(entity.GetIndexes(), i =>
            i.Properties.Select(p => p.Name).SequenceEqual(
                [nameof(Transaction.UserId), nameof(Transaction.WishlistItemId)]));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void Model_EnforcesOneTransactionPerRecurringOccurrence()
    {
        using var context = TestHelpers.NewInMemoryContext();
        var entity = context.Model.FindEntityType(typeof(Transaction))!;

        // This is the concurrency guard for pay-early / normal-confirmation races: a partial
        // unique index (null-exempt) so legacy/manual transactions are unaffected.
        var index = Assert.Single(entity.GetIndexes(), i =>
            i.Properties.Select(p => p.Name).SequenceEqual(
            [
                nameof(Transaction.UserId),
                nameof(Transaction.RecurringPaymentId),
                nameof(Transaction.RecurringOccurrenceDate)
            ]));

        Assert.True(index.IsUnique);
        Assert.Equal(
            "\"RecurringPaymentId\" IS NOT NULL AND \"RecurringOccurrenceDate\" IS NOT NULL",
            index.GetFilter());
    }

    [Fact]
    public void Model_EnforcesOnePushReminderDeliveryClaimPerOccurrenceAndSubscription()
    {
        using var context = TestHelpers.NewInMemoryContext();
        var entity = context.Model.FindEntityType(typeof(PushReminderDelivery))!;

        var index = Assert.Single(entity.GetIndexes(), i =>
            i.Properties.Select(p => p.Name).SequenceEqual(
            [
                nameof(PushReminderDelivery.UserId),
                nameof(PushReminderDelivery.RecurringPaymentId),
                nameof(PushReminderDelivery.OccurrenceDate),
                nameof(PushReminderDelivery.ActualOffsetDays),
                nameof(PushReminderDelivery.SubscriptionId)
            ]));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void Model_EnforcesOnePushSubscriptionPerUserAndDevice()
    {
        using var context = TestHelpers.NewInMemoryContext();
        var entity = context.Model.FindEntityType(typeof(PushSubscription))!;

        var index = Assert.Single(entity.GetIndexes(), i =>
            i.Properties.Select(p => p.Name).SequenceEqual(
                [nameof(PushSubscription.UserId), nameof(PushSubscription.DeviceId)]));

        Assert.True(index.IsUnique);
    }
}
