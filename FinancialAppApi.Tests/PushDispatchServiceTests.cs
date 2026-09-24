using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Push;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class PushDispatchServiceTests
{
    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenFcmProjectIdIsNotConfigured()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: today.Day, leadDays: 3));
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(today), sender, fcmProjectId: "");

        var summary = await service.DispatchAsync();

        Assert.Equal(0, summary.Sent);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(0, summary.Disabled);
        Assert.Empty(sender.Sent);
        await using var check = NewSeedContext(dbName);
        Assert.Empty(await check.PushReminderDeliveries.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task DispatchAsync_SendsForAnyEnabledDevice_WithNoAccountLevelFlagToConsult()
    {
        // The legacy account flag this used to have to override no longer exists: an enabled
        // device subscription is the whole opt-in.
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: today.AddDays(3).Day, leadDays: 3));
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(today), sender);

        var summary = await service.DispatchAsync();

        Assert.Equal(1, summary.Sent);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task DispatchAsync_SkipsPayment_WhenPaymentLevelReminderIsDisabled()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: today.Day, leadDays: 3, pushReminderEnabled: false));
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(today), sender);

        var summary = await service.DispatchAsync();

        Assert.Equal(0, summary.Sent);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task DispatchAsync_SkipsPayment_WhenInactive()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: today.Day, leadDays: 3, active: false));
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(today), sender);

        var summary = await service.DispatchAsync();

        Assert.Equal(0, summary.Sent);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task DispatchAsync_SkipsAccount_WhenNoSubscriptionIsEnabled()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: today.Day, leadDays: 3),
            subscriptions: [NewSubscription("sub-1", "token-1", enabled: false)]);
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(today), sender);

        var summary = await service.DispatchAsync();

        Assert.Equal(0, summary.Sent);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task DispatchAsync_Once_DoesNotSendOutsideTheExactSelectedOffset()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        // Due in 2 days, lead window is 3 days: inside the window but not offset 0.
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: today.AddDays(2).Day, leadDays: 3, mode: "Once"));
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(today), sender);

        var summary = await service.DispatchAsync();

        Assert.Equal(0, summary.Sent);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task DispatchAsync_Once_NeverResends_RegardlessOfTheOffsetItWasOriginallyClaimedAt()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: today.Day, leadDays: 3, mode: "Once"));
        // Simulate an earlier run that already claimed this occurrence at a different offset.
        await using (var seed = NewSeedContext(dbName))
        {
            seed.PushReminderDeliveries.Add(new PushReminderDelivery
            {
                Id = "prd-existing",
                UserId = "user-a",
                RecurringPaymentId = "rec-1",
                OccurrenceDate = today,
                ActualOffsetDays = 2,
                SubscriptionId = "sub-1",
                SentAt = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(today), sender);

        var summary = await service.DispatchAsync();

        Assert.Equal(0, summary.Sent);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task DispatchAsync_Countdown_SendsOncePerOffset_WithNoBackfillForAMissedDay()
    {
        var dbName = NewDbName();
        var dueDate = new DateOnly(2026, 7, 13);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: dueDate.Day, leadDays: 3, mode: "Daily"));
        // Offset 3 was already sent (e.g. three days ago); offset 2 was missed entirely
        // (the job didn't run that day). Today is offset 1.
        await using (var seed = NewSeedContext(dbName))
        {
            seed.PushReminderDeliveries.Add(new PushReminderDelivery
            {
                Id = "prd-offset-3",
                UserId = "user-a",
                RecurringPaymentId = "rec-1",
                OccurrenceDate = dueDate,
                ActualOffsetDays = 3,
                SubscriptionId = "sub-1",
                SentAt = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(dueDate.AddDays(-1)), sender);

        var summary = await service.DispatchAsync();

        // Countdown still sends today's (offset 1) reminder; it never backfills offset 2.
        Assert.Equal(1, summary.Sent);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("Due tomorrow", sent.Content.Body);
        await using var check = NewSeedContext(dbName);
        var deliveries = await check.PushReminderDeliveries.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, deliveries.Count);
        Assert.DoesNotContain(deliveries, d => d.ActualOffsetDays == 2);
    }

    [Fact]
    public async Task DispatchAsync_Countdown_DedupesTheSameOffsetOnASecondRunTheSameDay()
    {
        var dbName = NewDbName();
        var dueDate = new DateOnly(2026, 7, 13);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: dueDate.Day, leadDays: 3, mode: "Daily"));
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(dueDate.AddDays(-1)), sender);

        var first = await service.DispatchAsync();
        var second = await service.DispatchAsync();

        Assert.Equal(1, first.Sent);
        Assert.Equal(0, second.Sent);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task DispatchAsync_AutoDeductShortfall_SendsAlertOneDayPrior_EvenWhenPaymentReminderDisabled()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        var tomorrow = today.AddDays(1);
        var account = new LedgerAccount
        {
            Id = "acc-main",
            Name = "Checking Account",
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank
        };
        await SeedAsync(dbName, "user-a",
            NewPayment(
                "rec-rent",
                dueDate: tomorrow.Day,
                leadDays: 3,
                name: "Rent",
                amount: 1200m,
                pushReminderEnabled: false,
                paymentMode: RecurringPaymentMode.AutoDeduct,
                accountId: "acc-main"),
            accounts: [account]);

        // Seed an opening balance of 300 in Checking Account (shortfall = 900)
        await using (var seed = NewSeedContext(dbName))
        {
            seed.Transactions.Add(new Transaction
            {
                Id = "tx-open",
                UserId = "user-a",
                Date = new DateTime(2026, 7, 1),
                Description = "Initial balance",
                Category = "Income",
                LedgerCategory = "Essentials",
                AccountId = "acc-main",
                Amount = 300m
            });
            await seed.SaveChangesAsync();
        }

        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(today), sender);

        var summary = await service.DispatchAsync();

        Assert.Equal(1, summary.Sent);
        Assert.Single(sender.Sent);
        var message = sender.Sent[0].Content;
        Assert.Equal("Rent", message.Title);
        Assert.Contains("needs 900.00 more in Checking Account", message.Body);
        Assert.Contains("Auto-deducts tomorrow", message.Body);
    }

    [Fact]
    public async Task DispatchAsync_AutoDeductShortfall_DoesNotSendWhenBalanceIsSufficient()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        var tomorrow = today.AddDays(1);
        var account = new LedgerAccount
        {
            Id = "acc-main",
            Name = "Checking Account",
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank
        };
        await SeedAsync(dbName, "user-a",
            NewPayment(
                "rec-netflix",
                dueDate: tomorrow.Day,
                leadDays: 3,
                name: "Netflix",
                amount: 50m,
                pushReminderEnabled: false,
                paymentMode: RecurringPaymentMode.AutoDeduct,
                accountId: "acc-main"),
            accounts: [account]);

        // Seed 300 balance in Checking Account (bill is 50 -> balance is plenty, no shortfall)
        await using (var seed = NewSeedContext(dbName))
        {
            seed.Transactions.Add(new Transaction
            {
                Id = "tx-open",
                UserId = "user-a",
                Date = new DateTime(2026, 7, 1),
                Description = "Initial balance",
                Category = "Income",
                LedgerCategory = "Essentials",
                AccountId = "acc-main",
                Amount = 300m
            });
            await seed.SaveChangesAsync();
        }

        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(today), sender);

        var summary = await service.DispatchAsync();

        Assert.Equal(0, summary.Sent);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task DispatchAsync_AutoDeductShortfall_ProjectsEarlierBillsAgainstTheSameAccount()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        var tomorrow = today.AddDays(1);
        var account = new LedgerAccount
        {
            Id = "acc-main",
            Name = "Checking Account",
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank
        };
        await SeedAsync(dbName, "user-a",
            NewPayment(
                "rec-first",
                dueDate: tomorrow.Day,
                leadDays: 3,
                name: "First bill",
                amount: 500m,
                pushReminderEnabled: false,
                paymentMode: RecurringPaymentMode.AutoDeduct,
                accountId: account.Id),
            accounts: [account]);
        await using (var seed = NewSeedContext(dbName))
        {
            var second = NewPayment(
                "rec-second",
                dueDate: tomorrow.Day,
                leadDays: 3,
                name: "Second bill",
                amount: 450m,
                pushReminderEnabled: false,
                paymentMode: RecurringPaymentMode.AutoDeduct,
                accountId: account.Id);
            second.UserId = "user-a";
            seed.RecurringPayments.Add(second);
            seed.Transactions.Add(new Transaction
            {
                Id = "tx-open",
                UserId = "user-a",
                Date = new DateTime(2026, 7, 1),
                Description = "Initial balance",
                Category = "Income",
                LedgerCategory = "Essentials",
                AccountId = account.Id,
                Amount = 800m
            });
            await seed.SaveChangesAsync();
        }

        var sender = new FakeFcmPushSender();
        var summary = await NewDispatchService(dbName, Clock(today), sender).DispatchAsync();

        Assert.Equal(1, summary.Sent);
        Assert.Contains("needs 150.00 more in Checking Account", sender.Sent.Single().Content.Body);
        Assert.Equal("Second bill", sender.Sent.Single().Content.Title);
    }

    [Fact]
    public async Task DispatchAsync_AutoDeductShortfall_DoesNotSendTwoDaysPrior()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        var inTwoDays = today.AddDays(2);
        var account = new LedgerAccount
        {
            Id = "acc-main",
            Name = "Checking Account",
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank
        };
        await SeedAsync(dbName, "user-a",
            NewPayment(
                "rec-rent",
                dueDate: inTwoDays.Day,
                leadDays: 3,
                name: "Rent",
                amount: 1200m,
                pushReminderEnabled: false,
                paymentMode: RecurringPaymentMode.AutoDeduct,
                accountId: "acc-main"),
            accounts: [account]);

        // 300 balance in Checking Account, 2 days prior
        await using (var seed = NewSeedContext(dbName))
        {
            seed.Transactions.Add(new Transaction
            {
                Id = "tx-open",
                UserId = "user-a",
                Date = new DateTime(2026, 7, 1),
                Description = "Initial balance",
                Category = "Income",
                LedgerCategory = "Essentials",
                AccountId = "acc-main",
                Amount = 300m
            });
            await seed.SaveChangesAsync();
        }

        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(today), sender);

        var summary = await service.DispatchAsync();

        // 1 day prior only constraint
        Assert.Equal(0, summary.Sent);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task DispatchAsync_SkipsOccurrence_AlreadyPaidBeforeDispatchRuns()
    {
        // Regardless of the time of day the payment was recorded (e.g. 08:59, ahead of the
        // 09:00 scheduler run), the occurrence must never receive a reminder once it is paid.
        var dbName = NewDbName();
        var dueDate = new DateOnly(2026, 7, 10);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: dueDate.Day, leadDays: 3, mode: "Once"));
        await using (var seed = NewSeedContext(dbName))
        {
            seed.Transactions.Add(new Transaction
            {
                Id = "tx-paid",
                UserId = "user-a",
                Date = TransactionDate.FromInputDate(dueDate),
                Description = "Rent",
                Category = "Bills",
                LedgerCategory = "Essentials",
                Amount = 100m,
                RecurringPaymentId = "rec-1",
                RecurringOccurrenceDate = dueDate
            });
            await seed.SaveChangesAsync();
        }
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(dueDate), sender);

        var summary = await service.DispatchAsync();

        Assert.Equal(0, summary.Sent);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task DispatchAsync_LegacyFallback_TreatsANullOccurrenceDateTransactionOnTheSameDateAsPaid()
    {
        var dbName = NewDbName();
        var dueDate = new DateOnly(2026, 7, 10);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: dueDate.Day, leadDays: 3, mode: "Once"));
        // A legacy/manually-recorded transaction predating the RecurringOccurrenceDate column:
        // it is linked to the payment and dated on the occurrence, but never tagged.
        await using (var seed = NewSeedContext(dbName))
        {
            seed.Transactions.Add(new Transaction
            {
                Id = "tx-legacy",
                UserId = "user-a",
                Date = TransactionDate.FromInputDate(dueDate),
                Description = "Rent",
                Category = "Bills",
                LedgerCategory = "Essentials",
                Amount = 100m,
                RecurringPaymentId = "rec-1",
                RecurringOccurrenceDate = null
            });
            await seed.SaveChangesAsync();
        }
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(dueDate), sender);

        var summary = await service.DispatchAsync();

        Assert.Equal(0, summary.Sent);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task DispatchAsync_MonthlyFrequency_MatchesTheExactRecurrenceEngineOccurrence()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 8);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: 15, leadDays: 7, mode: "Once", frequency: "Monthly", startDate: "2026-01-01"));
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(today), sender);

        var summary = await service.DispatchAsync();

        Assert.Equal(1, summary.Sent);
        Assert.Equal("Due in 7 days", sender.Sent.Single().Content.Body);
    }

    [Fact]
    public async Task DispatchAsync_LegacyWeeklyFrequency_IsTreatedAsMonthly()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 8);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: 15, leadDays: 7, mode: "Once", frequency: "Weekly", startDate: "2026-01-01"));
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(today), sender);

        var summary = await service.DispatchAsync();

        // RecurringOccurrenceService documents this legacy alias: a "Weekly" payment is
        // resolved exactly like "Monthly", not skipped and not literally weekly.
        Assert.Equal(1, summary.Sent);
    }

    [Fact]
    public async Task DispatchAsync_IsolatesEachUsersReminders_InAMultiUserRun()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-a", dueDate: today.Day, leadDays: 3, mode: "Daily"),
            subscriptions: [NewSubscription("sub-a", "token-a")]);
        await SeedAsync(dbName, "user-b",
            NewPayment("rec-b", dueDate: today.Day, leadDays: 3, mode: "Daily"),
            subscriptions: [NewSubscription("sub-b", "token-b")]);
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(today), sender);

        var summary = await service.DispatchAsync();

        Assert.Equal(2, summary.Sent);
        Assert.Equal(2, sender.Sent.Count);
        Assert.Contains(sender.Sent, s => s.Token == "token-a");
        Assert.Contains(sender.Sent, s => s.Token == "token-b");

        await using var check = NewSeedContext(dbName);
        var deliveries = await check.PushReminderDeliveries.IgnoreQueryFilters().ToListAsync();
        Assert.Contains(deliveries, d => d.UserId == "user-a" && d.RecurringPaymentId == "rec-a");
        Assert.Contains(deliveries, d => d.UserId == "user-b" && d.RecurringPaymentId == "rec-b");
    }

    [Fact]
    public async Task DispatchAsync_IsolatesAnInvalidToken_WithoutAffectingOtherDevices()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: today.Day, leadDays: 3, mode: "Daily"),
            subscriptions: [NewSubscription("sub-bad", "token-bad"), NewSubscription("sub-good", "token-good")]);
        var sender = new FakeFcmPushSender
        {
            ResultForToken = token => token == "token-bad"
                ? new FcmSendResult(FcmSendStatus.InvalidOrUnregistered)
                : new FcmSendResult(FcmSendStatus.Sent)
        };
        var service = NewDispatchService(dbName, Clock(today), sender);

        var summary = await service.DispatchAsync();

        Assert.Equal(1, summary.Sent);
        Assert.Equal(1, summary.Disabled);
        Assert.Equal(2, sender.Sent.Count);

        await using var check = NewSeedContext(dbName);
        var subscriptions = await check.PushSubscriptions.IgnoreQueryFilters().ToListAsync();
        Assert.False(subscriptions.Single(s => s.Id == "sub-bad").Enabled);
        Assert.True(subscriptions.Single(s => s.Id == "sub-good").Enabled);
        var deliveries = await check.PushReminderDeliveries.IgnoreQueryFilters().ToListAsync();
        Assert.DoesNotContain(deliveries, delivery => delivery.SubscriptionId == "sub-bad");
        Assert.Contains(deliveries, delivery => delivery.SubscriptionId == "sub-good");
    }

    [Fact]
    public async Task DispatchAsync_SkipsADeviceThatOnlyAskedForSpendingAlerts()
    {
        // The reported multi-device failure mode in the other direction: a phone opted into
        // spending alerts must not start receiving bill reminders it never asked for, and the
        // desktop that did ask still gets exactly one.
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        var alertsOnly = NewSubscription("sub-alerts", "token-alerts");
        alertsOnly.BillRemindersEnabled = false;
        alertsOnly.CategoryAlertsEnabled = true;
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: today.Day, leadDays: 3, mode: "Daily"),
            subscriptions: [alertsOnly, NewSubscription("sub-bills", "token-bills")]);
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(today), sender);

        var summary = await service.DispatchAsync();

        Assert.Equal(1, summary.Sent);
        Assert.Equal("token-bills", Assert.Single(sender.Sent).Token);
    }

    [Fact]
    public async Task DispatchAsync_SendsNothingWhenNoDeviceWantsBillReminders()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        var alertsOnly = NewSubscription("sub-alerts", "token-alerts");
        alertsOnly.BillRemindersEnabled = false;
        alertsOnly.CategoryAlertsEnabled = true;
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: today.Day, leadDays: 3, mode: "Daily"),
            subscriptions: [alertsOnly]);
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(today), sender);

        var summary = await service.DispatchAsync();

        Assert.Equal(0, summary.Sent);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task DispatchAsync_TransientFailure_ReleasesClaimForSameDayRetry()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: today.Day, leadDays: 3, mode: "Daily"));
        var sender = new FakeFcmPushSender
        {
            ResultForToken = _ => new FcmSendResult(FcmSendStatus.TransientFailure)
        };
        var service = NewDispatchService(dbName, Clock(today), sender);

        var failed = await service.DispatchAsync();
        sender.ResultForToken = _ => new FcmSendResult(FcmSendStatus.Sent);
        var retried = await service.DispatchAsync();

        Assert.Equal(1, failed.Skipped);
        Assert.Equal(1, retried.Sent);
        await using var check = NewSeedContext(dbName);
        Assert.Single(await check.PushReminderDeliveries.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task DispatchAsync_PartialFailure_RetriesOnlyTheDeviceThatDidNotReceiveTheReminder()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: today.Day, leadDays: 3, mode: "Daily"),
            subscriptions:
            [
                NewSubscription("sub-good", "token-good"),
                NewSubscription("sub-retry", "token-retry")
            ]);
        var sender = new FakeFcmPushSender
        {
            ResultForToken = token => token == "token-retry"
                ? new FcmSendResult(FcmSendStatus.TransientFailure)
                : new FcmSendResult(FcmSendStatus.Sent)
        };
        var service = NewDispatchService(dbName, Clock(today), sender);

        var partial = await service.DispatchAsync();

        Assert.Equal(1, partial.Sent);
        Assert.Equal(1, partial.Failed);
        Assert.True(partial.RequiresRetry);
        Assert.Equal(2, sender.Sent.Count);

        sender.ResultForToken = _ => new FcmSendResult(FcmSendStatus.Sent);
        var retry = await service.DispatchAsync();

        Assert.Equal(1, retry.Sent);
        Assert.Equal(0, retry.Failed);
        Assert.False(retry.RequiresRetry);
        Assert.Equal("token-retry", Assert.Single(sender.Sent.Skip(2)).Token);

        await using var check = NewSeedContext(dbName);
        var deliveries = await check.PushReminderDeliveries.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, deliveries.Count);
        Assert.Contains(deliveries, delivery => delivery.SubscriptionId == "sub-good");
        Assert.Contains(deliveries, delivery => delivery.SubscriptionId == "sub-retry");
    }

    [Fact]
    public async Task DispatchAsync_IsolatesAnUnexpectedSenderException_WithoutAffectingOtherDevices()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: today.Day, leadDays: 3, mode: "Daily"),
            subscriptions: [NewSubscription("sub-throws", "token-throws"), NewSubscription("sub-good", "token-good")]);
        var sender = new FakeFcmPushSender
        {
            ThrowForToken = "token-throws"
        };
        var service = NewDispatchService(dbName, Clock(today), sender);

        var summary = await service.DispatchAsync();

        Assert.Equal(1, summary.Sent);
        Assert.True(summary.Skipped >= 1);
    }

    [Fact]
    public async Task DispatchAsync_Content_HasNoAmount_AStablePerOccurrenceTag_AndARecurringPaymentRoute()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", name: "Netflix", dueDate: today.Day, leadDays: 3, mode: "Daily", amount: 54.90m));
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(today), sender);

        await service.DispatchAsync();

        var content = sender.Sent.Single().Content;
        Assert.Equal("Netflix", content.Title);
        Assert.DoesNotContain("54.9", content.Title);
        Assert.DoesNotContain("54.9", content.Body);
        // PUSH-03: the same tag the client's buildNotificationTag() derives from this payload.
        Assert.Equal($"recurring-reminder-rec-1-{today:yyyy-MM-dd}", content.Tag);
        Assert.Equal("/recurring?subscription=rec-1", content.Route);
        Assert.Equal("recurring-payment", content.Kind);
        Assert.Equal("rec-1", content.Data["recurringPaymentId"]);
        Assert.Equal(today.ToString("yyyy-MM-dd"), content.Data["occurrenceDate"]);
    }

    [Fact]
    public async Task DispatchAsync_SendsDetailedBillReminderPreviews()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", name: "Netflix", dueDate: today.Day, leadDays: 3, mode: "Daily", amount: 54.90m),
            subscriptions: [NewSubscription("sub-1", "token-1")]);
        var sender = new FakeFcmPushSender();

        await NewDispatchService(dbName, Clock(today), sender).DispatchAsync();

        var content = sender.Sent.Single().Content;
        Assert.Equal("Netflix", content.Title);
        Assert.Equal("Due today", content.Body);
        Assert.DoesNotContain("54.90", content.Body);
        Assert.False(content.Data.ContainsKey("accountName"));
        Assert.False(content.Data.ContainsKey("shortfall"));
    }

    [Fact]
    public async Task DispatchAsync_Content_TimeToLive_EndsBeforeTheEndOfTheCurrentLocalDay()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        await SeedAsync(dbName, "user-a",
            NewPayment("rec-1", dueDate: today.Day, leadDays: 3, mode: "Daily"));
        var sender = new FakeFcmPushSender();
        var service = NewDispatchService(dbName, Clock(today), sender);

        await service.DispatchAsync();

        var ttl = sender.Sent.Single().Content.TimeToLive;
        Assert.True(ttl <= TimeSpan.FromDays(1));
        Assert.True(ttl > TimeSpan.Zero);
    }

    [Fact]
    public async Task DispatchAsync_AutoDeductShortfall_UsesTheOccurrenceSnapshotAmount_NotTheParent()
    {
        // The parent has since been re-priced down to 100, but the occurrence due tomorrow was
        // materialised at 1200 and that is what will actually be deducted. Reading the parent said
        // a 300 balance was plenty and sent nothing.
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        var tomorrow = today.AddDays(1);
        await SeedShortfallFixtureAsync(dbName, tomorrow, amount: 100m, balance: 300m);
        await using (var seed = NewSeedContext(dbName))
        {
            seed.RecurringPaymentOccurrences.Add(new RecurringPaymentOccurrence
            {
                Id = $"occ-rec-rent-{tomorrow:yyyyMMdd}",
                UserId = "user-a",
                RecurringPaymentId = "rec-rent",
                OccurrenceDate = tomorrow,
                Name = "Rent",
                ScheduledAmount = 1200m,
                LedgerCategory = "Essentials",
                AccountId = "acc-main",
                PaymentMode = RecurringPaymentMode.AutoDeduct,
                Status = RecurringOccurrenceStatus.Pending
            });
            await seed.SaveChangesAsync();
        }

        var sender = new FakeFcmPushSender();
        var summary = await NewDispatchService(dbName, Clock(today), sender).DispatchAsync();

        Assert.Equal(1, summary.Sent);
        Assert.Contains("needs 900.00 more in Checking Account", sender.Sent.Single().Content.Body);
    }

    [Fact]
    public async Task DispatchAsync_AutoDeductShortfall_TreatsANegativeStoredAmountAsItsMagnitude()
    {
        // Every other reader defends the sign with Math.Abs; comparing a 300 balance against a
        // stored -1200 made the shortfall unreachable for any positive balance.
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        await SeedShortfallFixtureAsync(dbName, today.AddDays(1), amount: -1200m, balance: 300m);

        var sender = new FakeFcmPushSender();
        var summary = await NewDispatchService(dbName, Clock(today), sender).DispatchAsync();

        Assert.Equal(1, summary.Sent);
        Assert.Contains("needs 900.00 more in Checking Account", sender.Sent.Single().Content.Body);
    }

    [Fact]
    public async Task DispatchAsync_AutoDeductShortfall_DoesNotFireWhenTheAccountCannotBeResolved()
    {
        // A missing account used to read as a zero balance, so every legacy auto-deduct bill
        // carrying no placement reported a full-amount shortfall against "account".
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        await SeedShortfallFixtureAsync(
            dbName, today.AddDays(1), amount: 1200m, balance: 300m, paymentAccountId: "acc-missing");

        var sender = new FakeFcmPushSender();
        var summary = await NewDispatchService(dbName, Clock(today), sender).DispatchAsync();

        Assert.Equal(0, summary.Sent);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task DispatchAsync_ShortfallAlert_DoesNotConsumeTheConfiguredOnceReminder()
    {
        // The shortfall goes out a day early; the bill's own "Once" reminder is configured for the
        // due date itself. They are two different messages, so claiming the first must not make the
        // offset-agnostic Once lookup believe the second was already sent.
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        var tomorrow = today.AddDays(1);
        await SeedShortfallFixtureAsync(
            dbName, tomorrow, amount: 1200m, balance: 300m, leadDays: 0, pushReminderEnabled: true);

        var sender = new FakeFcmPushSender();
        var dayBefore = await NewDispatchService(dbName, Clock(today), sender).DispatchAsync();

        Assert.Equal(1, dayBefore.Sent);
        Assert.Contains("Auto-deducts tomorrow", sender.Sent.Single().Content.Body);

        var dueDay = await NewDispatchService(dbName, Clock(tomorrow), sender).DispatchAsync();

        Assert.Equal(1, dueDay.Sent);
        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal("Due today", sender.Sent[1].Content.Body);
    }

    [Fact]
    public async Task DispatchAsync_ShortfallAlert_IsStillSentOnlyOncePerDay()
    {
        var dbName = NewDbName();
        var today = new DateOnly(2026, 7, 10);
        await SeedShortfallFixtureAsync(dbName, today.AddDays(1), amount: 1200m, balance: 300m);

        var sender = new FakeFcmPushSender();
        var first = await NewDispatchService(dbName, Clock(today), sender).DispatchAsync();
        var second = await NewDispatchService(dbName, Clock(today), sender).DispatchAsync();

        Assert.Equal(1, first.Sent);
        Assert.Equal(0, second.Sent);
        Assert.Single(sender.Sent);
    }

    private static async Task SeedShortfallFixtureAsync(
        string dbName,
        DateOnly dueOn,
        decimal amount,
        decimal balance,
        int leadDays = 3,
        bool pushReminderEnabled = false,
        string paymentAccountId = "acc-main")
    {
        await SeedAsync(dbName, "user-a",
            NewPayment(
                "rec-rent",
                dueDate: dueOn.Day,
                leadDays: leadDays,
                name: "Rent",
                amount: amount,
                pushReminderEnabled: pushReminderEnabled,
                paymentMode: RecurringPaymentMode.AutoDeduct,
                accountId: paymentAccountId),
            accounts:
            [
                new LedgerAccount
                {
                    Id = "acc-main",
                    Name = "Checking Account",
                    Bucket = "Essentials",
                    Kind = LedgerAccountKind.Bank
                }
            ]);

        await using var seed = NewSeedContext(dbName);
        seed.Transactions.Add(new Transaction
        {
            Id = "tx-open",
            UserId = "user-a",
            Date = new DateTime(2026, 7, 1),
            Description = "Initial balance",
            Category = "Income",
            LedgerCategory = "Essentials",
            AccountId = "acc-main",
            Amount = balance
        });
        await seed.SaveChangesAsync();
    }

    private static string NewDbName() => "push-dispatch-" + Guid.NewGuid().ToString("N");

    private static FinancialClock Clock(DateOnly today)
    {
        var configuration = TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC"));
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(today.ToDateTime(new TimeOnly(1, 0)), TimeSpan.Zero));
        return new FinancialClock(configuration, timeProvider);
    }

    private static AppDbContext NewSeedContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static PushDispatchService NewDispatchService(
        string dbName,
        FinancialClock clock,
        IFcmPushSender sender,
        string fcmProjectId = "test-project")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options =>
            options.UseInMemoryDatabase(dbName).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<FinancialAppApi.Services.Accounts.LedgerAccountBalanceService>();
        services.AddScoped<CycleBalanceService>();
        services.AddScoped<FinancialAppApi.Services.Accounts.LedgerAccountService>();
        services.AddScoped<RecurringOccurrenceService>();
        services.AddScoped<PushSubscriptionService>();
        // The dispatcher resolves the ledger per user scope. It must share the test's fixed
        // clock, or "which occurrence is next" is answered against the wall clock and the
        // seeded due dates never line up.
        services.AddSingleton(clock);
        services.AddScoped<RecurringOccurrenceLedgerService>();
        services.AddSingleton(sender);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        // Dispatch fails closed without a configured FCM project, so the fake still needs one set.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Fcm:ProjectId"] = "test-project" })
            .Build();

        return new PushDispatchService(scopeFactory, configuration, NullLogger<PushDispatchService>.Instance);
    }

    private static async Task SeedAsync(
        string dbName,
        string userId,
        RecurringPayment payment,
        int cycleDay = 1,
        PushSubscription[]? subscriptions = null,
        LedgerAccount[]? accounts = null)
    {
        await using var context = NewSeedContext(dbName);
        context.FinancialSettings.Add(new FinancialSetting
        {
            UserId = userId,
            CycleDay = cycleDay
        });
        payment.UserId = userId;
        context.RecurringPayments.Add(payment);
        foreach (var subscription in subscriptions ?? [NewSubscription("sub-1", "token-1")])
        {
            subscription.UserId = userId;
            context.PushSubscriptions.Add(subscription);
        }
        foreach (var account in accounts ?? [])
        {
            account.UserId = userId;
            context.LedgerAccounts.Add(account);
        }
        await context.SaveChangesAsync();
    }

    private static RecurringPayment NewPayment(
        string id,
        int dueDate,
        int leadDays,
        string name = "Payment",
        string mode = "Once",
        bool pushReminderEnabled = true,
        bool active = true,
        decimal amount = 100m,
        string frequency = "Monthly",
        string startDate = "2026-01-01",
        string paymentMode = RecurringPaymentMode.Manual,
        string accountId = "acc-1")
    {
        return new RecurringPayment
        {
            Id = id,
            Name = name,
            Amount = amount,
            Frequency = frequency,
            Category = "Bills",
            LedgerCategory = "Essentials",
            AccountId = accountId,
            PaymentMode = paymentMode,
            NextDueDate = startDate,
            DueDate = dueDate,
            StartDate = startDate,
            Active = active,
            PushReminderEnabled = pushReminderEnabled,
            PushReminderMode = mode,
            PushReminderLeadDays = leadDays
        };
    }

    private static PushSubscription NewSubscription(
        string id,
        string token,
        bool enabled = true)
    {
        return new PushSubscription
        {
            Id = id,
            DeviceId = id,
            FcmToken = token,
            Enabled = enabled,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakeFcmPushSender : IFcmPushSender
    {
        public List<(string Token, PushNotificationContent Content)> Sent { get; } = [];
        public Func<string, FcmSendResult>? ResultForToken { get; set; }
        public string? ThrowForToken { get; set; }

        public Task<FcmSendResult> SendAsync(string fcmToken, PushNotificationContent content, CancellationToken cancellationToken = default)
        {
            if (ThrowForToken != null && fcmToken == ThrowForToken)
            {
                throw new InvalidOperationException("Simulated unexpected sender failure.");
            }

            Sent.Add((fcmToken, content));
            var result = ResultForToken?.Invoke(fcmToken) ?? new FcmSendResult(FcmSendStatus.Sent);
            return Task.FromResult(result);
        }
    }
}
