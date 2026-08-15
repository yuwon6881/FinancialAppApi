using FinancialAppApi.Models;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

public sealed class RecurringAccountShortfallEvaluatorTests
{
    [Fact]
    public void EvaluatesShortfalls_ForUpcomingAutoDeductOccurrencesWithInsufficientBalance()
    {
        var today = new DateOnly(2026, 8, 15);
        var accounts = new List<LedgerAccount>
        {
            new() { Id = "acct-1", Name = "Main Bank", Bucket = "Essentials", Kind = LedgerAccountKind.Bank },
            new() { Id = "acct-2", Name = "Savings", Bucket = "Stability", Kind = LedgerAccountKind.Bank }
        };
        var accountBalances = new Dictionary<string, decimal>
        {
            ["acct-1"] = 50m,
            ["acct-2"] = 1000m
        };

        var allRecurring = new List<RecurringPayment>
        {
            new()
            {
                Id = "rec-auto-shortfall",
                Name = "Electric Bill",
                Amount = 150m,
                PaymentMode = RecurringPaymentMode.AutoDeduct,
                AccountId = "acct-1",
                LedgerCategory = "Essentials"
            },
            new()
            {
                Id = "rec-manual",
                Name = "Gym Membership",
                Amount = 100m,
                PaymentMode = RecurringPaymentMode.Manual,
                AccountId = "acct-1",
                LedgerCategory = "Essentials"
            },
            new()
            {
                Id = "rec-auto-funded",
                Name = "Phone Bill",
                Amount = 200m,
                PaymentMode = RecurringPaymentMode.AutoDeduct,
                AccountId = "acct-2",
                LedgerCategory = "Stability"
            },
            new()
            {
                Id = "rec-later-month",
                Name = "Water Bill",
                Amount = 80m,
                PaymentMode = RecurringPaymentMode.AutoDeduct,
                AccountId = "acct-1",
                LedgerCategory = "Essentials"
            }
        };

        var occurrences = new List<RecurringPaymentOccurrence>
        {
            new()
            {
                Id = "occ-1",
                RecurringPaymentId = "rec-auto-shortfall",
                Name = "Electric Bill",
                ScheduledAmount = 150m,
                OccurrenceDate = today.AddDays(5),
                Status = RecurringOccurrenceStatus.Pending,
                AccountId = "acct-1"
            },
            new()
            {
                Id = "occ-2",
                RecurringPaymentId = "rec-manual",
                Name = "Gym Membership",
                ScheduledAmount = 100m,
                OccurrenceDate = today.AddDays(2),
                Status = RecurringOccurrenceStatus.Pending,
                AccountId = "acct-1"
            },
            new()
            {
                Id = "occ-3",
                RecurringPaymentId = "rec-auto-funded",
                Name = "Phone Bill",
                ScheduledAmount = 200m,
                OccurrenceDate = today.AddDays(1),
                Status = RecurringOccurrenceStatus.Pending,
                AccountId = "acct-2"
            },
            new()
            {
                Id = "occ-4",
                RecurringPaymentId = "rec-auto-shortfall",
                Name = "Electric Bill",
                ScheduledAmount = 150m,
                OccurrenceDate = today.AddDays(-1), // past
                Status = RecurringOccurrenceStatus.Pending,
                AccountId = "acct-1"
            },
            new()
            {
                Id = "occ-5",
                RecurringPaymentId = "rec-auto-shortfall",
                Name = "Electric Bill",
                ScheduledAmount = 150m,
                OccurrenceDate = today.AddDays(35), // > 31 days away
                Status = RecurringOccurrenceStatus.Pending,
                AccountId = "acct-1"
            },
            new()
            {
                Id = "occ-6",
                RecurringPaymentId = "rec-auto-shortfall",
                Name = "Electric Bill",
                ScheduledAmount = 150m,
                OccurrenceDate = today.AddDays(3),
                Status = RecurringOccurrenceStatus.Paid, // already paid
                AccountId = "acct-1"
            },
            new()
            {
                Id = "occ-7",
                RecurringPaymentId = "rec-later-month",
                Name = "Water Bill",
                ScheduledAmount = 80m,
                OccurrenceDate = today.AddDays(2),
                Status = RecurringOccurrenceStatus.Pending,
                AccountId = null // should fall back to parent AccountId ("acct-1")
            }
        };

        var shortfalls = RecurringAccountShortfallEvaluator.EvaluateShortfalls(
            occurrences,
            allRecurring,
            accounts,
            accountBalances,
            today);

        // Expected: occ-7 (2 days away, shortfall = 80 - 50 = 30), then occ-1 (5 days away, shortfall = 150 - 50 = 100)
        Assert.Equal(2, shortfalls.Count);

        dynamic first = shortfalls[0];
        Assert.Equal("rec-later-month", (string)first.recurringPaymentId);
        Assert.Equal(2, (int)first.offsetDays);
        Assert.Equal("acct-1", (string)first.accountId);
        Assert.Equal("Main Bank", (string)first.accountName);

        dynamic second = shortfalls[1];
        Assert.Equal("rec-auto-shortfall", (string)second.recurringPaymentId);
        Assert.Equal(5, (int)second.offsetDays);
        Assert.Equal("acct-1", (string)second.accountId);
    }
}
