using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class RecurringPaymentPayEarlyServiceTests
{
    [Fact]
    public async Task PayEarlyAsync_ReturnsPaymentNotFound_WhenPaymentMissing()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = NewService(context, Today(2026, 7, 10));

        var result = await service.PayEarlyAsync("missing");

        Assert.Equal(PayEarlyStatus.PaymentNotFound, result.Status);
        Assert.Empty(context.Transactions);
    }

    [Fact]
    public async Task PayEarlyAsync_ReturnsPaymentInactive_WhenPaymentIsNotActive()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", active: false));
        await context.SaveChangesAsync();
        var service = NewService(context, Today(2026, 7, 10));

        var result = await service.PayEarlyAsync("rec-1");

        Assert.Equal(PayEarlyStatus.PaymentInactive, result.Status);
        Assert.Empty(context.Transactions);
    }

    [Fact]
    public async Task PayEarlyAsync_SettlesNextFutureOccurrence_WithExactAmountCategoryAndTransactionDate_Monthly()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment(
            "rec-1",
            amount: 49.99m,
            category: "Bills",
            ledgerCategory: "Essentials",
            startDate: "2026-01-01",
            dueDate: 15));
        await context.SaveChangesAsync();
        var service = NewService(context, Today(2026, 7, 10));

        var result = await service.PayEarlyAsync("rec-1");

        Assert.Equal(PayEarlyStatus.Success, result.Status);
        Assert.Equal(new DateOnly(2026, 7, 15), result.SettledOccurrenceDate);

        var transaction = result.Transaction!;
        Assert.Equal(49.99m, transaction.Amount);
        Assert.Equal("Bills", transaction.Category);
        Assert.Equal("Essentials", transaction.LedgerCategory);
        Assert.Equal("rec-1", transaction.RecurringPaymentId);
        Assert.Equal(new DateOnly(2026, 7, 15), transaction.RecurringOccurrenceDate);
        // The transaction is posted as of today (the pay-early moment), not the occurrence date.
        Assert.Equal(TransactionDate.FromInputDate(new DateOnly(2026, 7, 10)), transaction.Date);
        Assert.Single(context.Transactions);
    }

    [Fact]
    public async Task PayEarlyAsync_SettlesNextFutureOccurrence_Annually()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment(
            "rec-1",
            frequency: "Annually",
            startDate: "2020-07-20",
            dueDate: 20));
        await context.SaveChangesAsync();
        var service = NewService(context, Today(2026, 7, 10));

        var result = await service.PayEarlyAsync("rec-1");

        Assert.Equal(PayEarlyStatus.Success, result.Status);
        Assert.Equal(new DateOnly(2026, 7, 20), result.SettledOccurrenceDate);
        Assert.Equal(new DateOnly(2026, 7, 20), result.Transaction!.RecurringOccurrenceDate);
    }

    [Fact]
    public async Task PayEarlyAsync_SkipsAlreadySettledOccurrence_AndSettlesTheNextOne_Monthly()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", startDate: "2026-01-01", dueDate: 15));
        // Simulate the current cycle's occurrence already having been paid (e.g. normal
        // confirmation, or a previous pay-early call).
        context.Transactions.Add(new Transaction
        {
            Id = "tx-existing",
            Date = TransactionDate.FromInputDate(new DateOnly(2026, 7, 15)),
            Description = "Rent",
            Category = "Bills",
            LedgerCategory = "Essentials",
            Amount = 100m,
            RecurringPaymentId = "rec-1",
            RecurringOccurrenceDate = new DateOnly(2026, 7, 15)
        });
        await context.SaveChangesAsync();
        var service = NewService(context, Today(2026, 7, 10));

        var result = await service.PayEarlyAsync("rec-1");

        Assert.Equal(PayEarlyStatus.Success, result.Status);
        Assert.Equal(new DateOnly(2026, 8, 15), result.SettledOccurrenceDate);
        Assert.Equal(2, await context.Transactions.CountAsync());
    }

    [Fact]
    public async Task PayEarlyAsync_ReturnsNoUpcomingOccurrence_WhenTheRecurrenceHasEnded()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment(
            "rec-1",
            startDate: "2026-01-01",
            dueDate: 15,
            endDate: "2026-06-30"));
        await context.SaveChangesAsync();
        var service = NewService(context, Today(2026, 7, 10));

        var result = await service.PayEarlyAsync("rec-1");

        Assert.Equal(PayEarlyStatus.NoUpcomingOccurrence, result.Status);
        Assert.Empty(context.Transactions);
    }

    [Fact]
    public async Task PayEarlyAsync_NeverDoubleSettlesTheOnlyOccurrence_WhenCalledTwice()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        // Only one occurrence ever exists for this payment (the recurrence window closes right
        // after its single due date), so a second pay-early call must find nothing left to settle
        // rather than creating a duplicate transaction for the same occurrence.
        context.RecurringPayments.Add(NewPayment(
            "rec-1",
            startDate: "2026-07-01",
            dueDate: 15,
            endDate: "2026-07-20"));
        await context.SaveChangesAsync();
        var service = NewService(context, Today(2026, 7, 10));

        var first = await service.PayEarlyAsync("rec-1");
        var second = await service.PayEarlyAsync("rec-1");

        Assert.Equal(PayEarlyStatus.Success, first.Status);
        Assert.Equal(PayEarlyStatus.NoUpcomingOccurrence, second.Status);
        Assert.Single(context.Transactions);
    }

    [Fact]
    public async Task PayEarlyAsync_ReplayedExpectedOccurrence_DoesNotPayTheFollowingMonth()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", dueDate: 15));
        await context.SaveChangesAsync();
        var service = NewService(context, Today(2026, 7, 10));
        var expected = new DateOnly(2026, 7, 15);

        var first = await service.PayEarlyAsync("rec-1", expected);
        var replay = await service.PayEarlyAsync("rec-1", expected);

        Assert.Equal(PayEarlyStatus.Success, first.Status);
        Assert.Equal(PayEarlyStatus.Conflict, replay.Status);
        Assert.Single(context.Transactions);
        Assert.Equal(new DateOnly(2026, 8, 15), first.NextOccurrenceDate);
    }

    [Fact]
    public async Task GetNextUnpaidOccurrenceAsync_IncludesDueTodayButSkipsItAfterSettlement()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", dueDate: 10));
        await context.SaveChangesAsync();
        var service = NewService(context, Today(2026, 7, 10));

        Assert.Equal(new DateOnly(2026, 7, 10), await service.GetNextUnpaidOccurrenceAsync("rec-1"));
        context.Transactions.Add(new Transaction
        {
            Id = "tx-paid",
            Date = TransactionDate.FromInputDate(new DateOnly(2026, 7, 10)),
            Description = "Payment",
            Category = "Bills",
            LedgerCategory = "Essentials",
            Amount = 100m,
            RecurringPaymentId = "rec-1",
            RecurringOccurrenceDate = new DateOnly(2026, 7, 10)
        });
        await context.SaveChangesAsync();

        Assert.Equal(new DateOnly(2026, 8, 10), await service.GetNextUnpaidOccurrenceAsync("rec-1"));
    }

    [Fact]
    public async Task PayEarlyAsync_IsScopedToTheOwningUser_ReturnsNotFoundForAnotherUsersPayment()
    {
        await using var context = TestHelpers.NewInMemoryContext(currentUserId: "user-a");
        context.RecurringPayments.Add(NewPayment("rec-1", startDate: "2026-01-01", dueDate: 15));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        // Same underlying data, different tenant: the global query filter must hide user-a's
        // recurring payment from user-b entirely.
        context.SetCurrentUser("user-b");
        var service = NewService(context, Today(2026, 7, 10));

        var result = await service.PayEarlyAsync("rec-1");

        Assert.Equal(PayEarlyStatus.PaymentNotFound, result.Status);
        Assert.Empty(context.Transactions);
    }

    private static RecurringPaymentPayEarlyService NewService(AppDbContext context, FinancialClock clock)
    {
        var occurrenceService = new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        return new RecurringPaymentPayEarlyService(context, occurrenceService, clock);
    }

    private static FinancialClock Today(int year, int month, int day)
    {
        var configuration = TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC"));
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero));
        return new FinancialClock(configuration, timeProvider);
    }

    private static RecurringPayment NewPayment(
        string id,
        decimal amount = 100m,
        string category = "Bills",
        string ledgerCategory = "Essentials",
        string frequency = "Monthly",
        string startDate = "2026-01-01",
        int dueDate = 15,
        string? endDate = null,
        bool active = true)
    {
        return new RecurringPayment
        {
            Id = id,
            Name = "Payment",
            Amount = amount,
            Frequency = frequency,
            Category = category,
            LedgerCategory = ledgerCategory,
            NextDueDate = startDate,
            DueDate = dueDate,
            StartDate = startDate,
            EndDate = endDate,
            Active = active
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
