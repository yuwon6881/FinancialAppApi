using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

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
    public void Model_EnforcesOneLiveDefaultPerBucketAndValidAccountShape()
    {
        using var context = TestHelpers.NewInMemoryContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(LedgerAccount))!;

        var index = Assert.Single(entity.GetIndexes(), i =>
            i.Properties.Select(p => p.Name).SequenceEqual(
                [nameof(LedgerAccount.UserId), nameof(LedgerAccount.Bucket)]));
        Assert.True(index.IsUnique);
        Assert.Equal("\"IsDefault\"", index.GetFilter());

        var bucketConstraint = Assert.Single(entity.GetCheckConstraints(), constraint => constraint.Name == "ck_ledgeraccounts_bucket");
        var kindConstraint = Assert.Single(entity.GetCheckConstraints(), constraint => constraint.Name == "ck_ledgeraccounts_kind");
        Assert.Equal("\"Bucket\" IN ('Essentials', 'Growth', 'Stability', 'Rewards')", bucketConstraint.Sql);
        Assert.Equal("\"Kind\" IN ('Bank', 'EWallet', 'Cash', 'Card', 'Other')", kindConstraint.Sql);

        var transactionEntity = model.FindEntityType(typeof(Transaction))!;
        Assert.Contains(transactionEntity.GetCheckConstraints(), constraint => constraint.Name == "ck_transactions_account_tracking");
        Assert.Contains(transactionEntity.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(
                [nameof(Transaction.UserId), nameof(Transaction.AccountId)]));
        Assert.Contains(transactionEntity.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(
                [nameof(Transaction.UserId), nameof(Transaction.CounterAccountId)]));
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
    public void Model_ConstrainsRecurringPaymentModeToTheTwoSupportedValues()
    {
        using var context = TestHelpers.NewInMemoryContext();
        // Check constraints live only in the design-time model, not the read-optimized runtime one.
        var entity = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(RecurringPayment))!;

        var constraint = Assert.Single(
            entity.GetCheckConstraints(),
            c => c.Name == "ck_recurringpayments_paymentmode");

        Assert.Equal("\"PaymentMode\" IN ('AutoDeduct', 'Manual')", constraint.Sql);
        // The database is the backstop for the controller's TryNormalizePaymentMode, so the two
        // must list the same values.
        Assert.Equal(RecurringPaymentMode.Manual, entity.FindProperty(
            nameof(RecurringPayment.PaymentMode))!.GetDefaultValue());
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
