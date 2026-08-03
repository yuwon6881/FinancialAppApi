using System.Text.Json;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class RecurringPaymentAlertServiceTests
{
    [Fact]
    public async Task GetSubscriptionAlertsAsync_DoesNotAlertForOccurrencePaidBeforeItsCycle()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 28 });
        context.RecurringPayments.Add(MonthlyHousehold());
        context.Transactions.Add(new Transaction
        {
            Id = "household-payearly",
            Date = new DateTime(2026, 7, 25),
            Description = "Household",
            Amount = -870m,
            Category = "HouseHold",
            LedgerCategory = "Stability",
            RecurringPaymentId = "household",
            RecurringOccurrenceDate = new DateOnly(2026, 7, 28)
        });
        await context.SaveChangesAsync();

        var alerts = await NewService(context, new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero))
            .GetSubscriptionAlertsAsync();

        Assert.Empty(alerts);
    }

    [Fact]
    public async Task GetSubscriptionAlertsAsync_UntaggedLegacyTransactionStillSettlesItsPostingCycle()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 28 });
        context.RecurringPayments.Add(MonthlyHousehold());
        context.Transactions.Add(new Transaction
        {
            Id = "household-legacy",
            Date = new DateTime(2026, 7, 28),
            Description = "Household",
            Amount = -870m,
            Category = "HouseHold",
            LedgerCategory = "Stability",
            RecurringPaymentId = "household"
        });
        await context.SaveChangesAsync();

        var alerts = await NewService(context, new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero))
            .GetSubscriptionAlertsAsync();

        Assert.Empty(alerts);
    }

    [Fact]
    public async Task GetSubscriptionAlertsAsync_AnnualPaymentAlertsInItsBillingMonth()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.RecurringPayments.Add(AnnualInsurance());
        await context.SaveChangesAsync();

        var alerts = await NewService(context, new DateTimeOffset(2028, 5, 15, 12, 0, 0, TimeSpan.Zero))
            .GetSubscriptionAlertsAsync();

        var alert = Assert.Single(alerts);
        var json = JsonSerializer.SerializeToElement(alert);
        Assert.Equal("insurance", json.GetProperty("recurringPaymentId").GetString());
        Assert.Equal("2028-05-15", json.GetProperty("billingDate").GetString());
    }

    [Fact]
    public async Task GetSubscriptionAlertsAsync_AnnualPaymentDoesNotAlertOutsideItsBillingMonth()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.RecurringPayments.Add(AnnualInsurance("2029-05-15"));
        await context.SaveChangesAsync();

        var alerts = await NewService(context, new DateTimeOffset(2029, 4, 30, 12, 0, 0, TimeSpan.Zero))
            .GetSubscriptionAlertsAsync();

        Assert.Empty(alerts);
    }

    [Fact]
    public async Task GetSubscriptionAlertsAsync_AnnualPaymentPaidEarlyDoesNotAlert()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.RecurringPayments.Add(AnnualInsurance("2029-05-15"));
        context.Transactions.Add(new Transaction
        {
            Id = "insurance-payearly",
            Date = new DateTime(2029, 5, 10),
            Description = "Insurance",
            Amount = -1200m,
            Category = "Insurance",
            LedgerCategory = "Stability",
            RecurringPaymentId = "insurance",
            RecurringOccurrenceDate = new DateOnly(2029, 5, 15)
        });
        await context.SaveChangesAsync();

        var alerts = await NewService(context, new DateTimeOffset(2029, 5, 15, 12, 0, 0, TimeSpan.Zero))
            .GetSubscriptionAlertsAsync();

        Assert.Empty(alerts);
    }

    private static RecurringPayment MonthlyHousehold() => new()
    {
        Id = "household",
        Name = "Household",
        Amount = 870m,
        Frequency = "Monthly",
        Category = "HouseHold",
        LedgerCategory = "Stability",
        StartDate = "2026-07-28",
        NextDueDate = "2026-07-28",
        DueDate = 28,
        Active = true
    };

    private static RecurringPayment AnnualInsurance(string startDate = "2028-05-15") => new()
    {
        Id = "insurance",
        Name = "Insurance",
        Amount = 1200m,
        Frequency = "Annually",
        Category = "Insurance",
        LedgerCategory = "Stability",
        StartDate = startDate,
        NextDueDate = startDate,
        DueDate = 15,
        Active = true
    };

    private static RecurringPaymentAlertService NewService(
        Database.AppDbContext context,
        DateTimeOffset utcNow)
    {
        var clock = new FinancialClock(
            TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")),
            new FixedTimeProvider(utcNow));
        return new RecurringPaymentAlertService(
            context,
            new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance),
            clock);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
