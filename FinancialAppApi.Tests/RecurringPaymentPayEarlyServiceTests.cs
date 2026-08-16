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

        var result = await service.PayEarlyAsync("rec-1", accountId: "acct-essentials");

        Assert.Equal(PayEarlyStatus.PaymentInactive, result.Status);
        Assert.Empty(context.Transactions);
    }

    [Fact]
    public async Task PayEarlyAsync_ReturnsAutomaticPayment_WhenBillIsAutoDeducted()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        // A perfectly payable occurrence exists -- only the payment mode stands in the way.
        context.RecurringPayments.Add(NewPayment(
            "rec-1",
            startDate: "2026-01-01",
            dueDate: 15,
            paymentMode: RecurringPaymentMode.AutoDeduct));
        await context.SaveChangesAsync();
        var service = NewService(context, Today(2026, 7, 10));

        var result = await service.PayEarlyAsync("rec-1", accountId: "acct-essentials");

        Assert.Equal(PayEarlyStatus.AutomaticPayment, result.Status);
        Assert.Empty(context.Transactions);
    }

    [Fact]
    public async Task PayEarlyAsync_ReturnsAutomaticPayment_WhenAutoDeductedAndTheClientNamesTheOccurrence()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment(
            "rec-1",
            startDate: "2026-01-01",
            dueDate: 15,
            paymentMode: RecurringPaymentMode.AutoDeduct));
        await context.SaveChangesAsync();
        var service = NewService(context, Today(2026, 7, 10));

        var result = await service.PayEarlyAsync("rec-1", new DateOnly(2026, 7, 15), accountId: "acct-essentials");

        Assert.Equal(PayEarlyStatus.AutomaticPayment, result.Status);
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

        var result = await service.PayEarlyAsync("rec-1", accountId: "acct-essentials");

        Assert.Equal(PayEarlyStatus.Success, result.Status);
        Assert.Equal(new DateOnly(2026, 7, 15), result.SettledOccurrenceDate);

        var transaction = result.Transaction!;
        Assert.Equal(-49.99m, transaction.Amount);
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

        var result = await service.PayEarlyAsync("rec-1", accountId: "acct-essentials");

        Assert.Equal(PayEarlyStatus.Success, result.Status);
        Assert.Equal(new DateOnly(2026, 7, 20), result.SettledOccurrenceDate);
        Assert.Equal(new DateOnly(2026, 7, 20), result.Transaction!.RecurringOccurrenceDate);
    }

    [Fact]
    public async Task PayEarlyAsync_InvalidatesCachedCycleBalancesFromThePostingCycle()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.RecurringPayments.Add(NewPayment("rec-1", dueDate: 15));
        context.CycleBalances.AddRange(
            new CycleBalance { Year = 2026, MonthIndex = 6, EssentialsBalance = 10m },
            new CycleBalance { Year = 2026, MonthIndex = 7, EssentialsBalance = 20m },
            new CycleBalance { Year = 2026, MonthIndex = 8, EssentialsBalance = 30m });
        await context.SaveChangesAsync();
        var service = NewService(context, Today(2026, 7, 10));

        var result = await service.PayEarlyAsync("rec-1", accountId: "acct-essentials");

        Assert.Equal(PayEarlyStatus.Success, result.Status);
        var snapshots = await context.CycleBalances.OrderBy(value => value.MonthIndex).ToListAsync();
        Assert.Collection(snapshots, snapshot => Assert.Equal(6, snapshot.MonthIndex));
    }

    [Fact]
    public async Task PayEarlyAsync_SkipsAlreadySettledOccurrence_AndSettlesTheNextOne_Monthly()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var payment = NewPayment("rec-1", startDate: "2026-01-01", dueDate: 15);
        context.RecurringPayments.Add(payment);
        // Simulate the current cycle's occurrence already having been paid (e.g. normal
        // confirmation, or a previous pay-early call).
        var existing = new Transaction
        {
            Id = "tx-existing",
            Date = TransactionDate.FromInputDate(new DateOnly(2026, 7, 15)),
            Description = "Rent",
            Category = "Bills",
            LedgerCategory = "Essentials",
            Amount = 100m,
            RecurringPaymentId = "rec-1",
            RecurringOccurrenceDate = new DateOnly(2026, 7, 15)
        };
        context.Transactions.Add(existing);
        context.RecurringPaymentOccurrences.Add(
            PaidOccurrence(payment, new DateOnly(2026, 7, 15), existing));
        await context.SaveChangesAsync();
        var service = NewService(context, Today(2026, 7, 10));

        var result = await service.PayEarlyAsync("rec-1", accountId: "acct-essentials");

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

        var result = await service.PayEarlyAsync("rec-1", accountId: "acct-essentials");

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

        var first = await service.PayEarlyAsync("rec-1", accountId: "acct-essentials");
        var second = await service.PayEarlyAsync("rec-1", accountId: "acct-essentials");

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

        var first = await service.PayEarlyAsync("rec-1", expected, accountId: "acct-essentials");
        var replay = await service.PayEarlyAsync("rec-1", expected, accountId: "acct-essentials");

        Assert.Equal(PayEarlyStatus.Success, first.Status);
        Assert.Equal(PayEarlyStatus.Conflict, replay.Status);
        Assert.Single(context.Transactions);
        Assert.Equal(new DateOnly(2026, 8, 15), first.NextOccurrenceDate);
    }

    [Fact]
    public async Task PayEarlyAsync_ReplayedClientKey_ReturnsTheOriginalTransaction()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", dueDate: 15));
        await context.SaveChangesAsync();
        var service = NewService(context, Today(2026, 7, 10));
        var expected = new DateOnly(2026, 7, 15);

        var first = await service.PayEarlyAsync("rec-1", expected, CancellationToken.None, "outbox-op-1", accountId: "acct-essentials");
        var replay = await service.PayEarlyAsync("rec-1", expected, CancellationToken.None, "outbox-op-1", accountId: "acct-essentials");

        Assert.Equal(PayEarlyStatus.Success, first.Status);
        Assert.Equal(PayEarlyStatus.Success, replay.Status);
        Assert.Equal(first.Transaction!.Id, replay.Transaction!.Id);
        Assert.Equal(first.SettledOccurrenceDate, replay.SettledOccurrenceDate);
        Assert.Single(context.Transactions);
    }

    [Fact]
    public async Task GetNextUnpaidOccurrencesAsync_IncludesDueTodayButSkipsItAfterSettlement()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", dueDate: 10));
        await context.SaveChangesAsync();
        var service = NewService(context, Today(2026, 7, 10));
        var projections = await new RecurringPaymentService(context).GetRecurringPaymentsAsync();

        var first = await service.GetNextUnpaidOccurrencesAsync(projections);
        Assert.Equal(new DateOnly(2026, 7, 10), first["rec-1"]);
        var settlement = new Transaction
        {
            Id = "tx-paid",
            Date = TransactionDate.FromInputDate(new DateOnly(2026, 7, 10)),
            Description = "Payment",
            Category = "Bills",
            LedgerCategory = "Essentials",
            Amount = 100m,
            RecurringPaymentId = "rec-1",
            RecurringOccurrenceDate = new DateOnly(2026, 7, 10)
        };
        context.Transactions.Add(settlement);
        // The first lookup above materialised this occurrence as pending, so settle that row
        // rather than adding a second one -- exactly what the settlement service does.
        RecurringOccurrenceLedgerService.SettleFromTransaction(
            await context.RecurringPaymentOccurrences.SingleAsync(
                occurrence => occurrence.OccurrenceDate == new DateOnly(2026, 7, 10)),
            settlement);
        await context.SaveChangesAsync();

        var next = await service.GetNextUnpaidOccurrencesAsync(projections);
        Assert.Equal(new DateOnly(2026, 8, 10), next["rec-1"]);
    }

    // The batch overload exists purely to stop the list endpoints issuing three queries per row.
    [Fact]
    public async Task GetNextUnpaidOccurrencesAsync_ResolvesEveryActivePayment()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var settled = NewPayment("rec-1", dueDate: 10);
        context.RecurringPayments.AddRange(
            settled,
            NewPayment("rec-2", dueDate: 20),
            NewPayment("rec-3", dueDate: 5, frequency: "Annually", startDate: "2026-03-01"),
            NewPayment("rec-ended", dueDate: 10, endDate: "2026-02-01"),
            NewPayment("rec-inactive", dueDate: 10, active: false));
        var settlement = new Transaction
        {
            Id = "tx-paid",
            Date = TransactionDate.FromInputDate(new DateOnly(2026, 7, 10)),
            Description = "Payment",
            Category = "Bills",
            LedgerCategory = "Essentials",
            Amount = 100m,
            RecurringPaymentId = "rec-1",
            RecurringOccurrenceDate = new DateOnly(2026, 7, 10)
        };
        context.Transactions.Add(settlement);
        context.RecurringPaymentOccurrences.Add(
            PaidOccurrence(settled, new DateOnly(2026, 7, 10), settlement));
        await context.SaveChangesAsync();

        var service = NewService(context, Today(2026, 7, 10));
        var projections = await new RecurringPaymentService(context).GetRecurringPaymentsAsync();
        var batch = await service.GetNextUnpaidOccurrencesAsync(projections);

        // Guards the settled-occurrence grouping: a settlement on one payment must not be
        // read as a settlement on another.
        Assert.Equal(new DateOnly(2026, 8, 10), batch["rec-1"]);
        Assert.Equal(new DateOnly(2026, 7, 20), batch["rec-2"]);
        Assert.False(batch.ContainsKey("rec-inactive"));
        Assert.False(batch.ContainsKey("rec-ended"));
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

    /// <summary>
    /// Identity is checked against the occurrence's own frozen account rather than the schedule's
    /// current one. Sending the parent's newer account for an occurrence that was materialised
    /// against the old one is a stale request, not a valid payment.
    /// </summary>
    [Fact]
    public async Task PayEarlyAsync_RefusesAnAccountTheOccurrenceWasNotScheduledAgainst()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", startDate: "2026-01-01", dueDate: 15));
        context.LedgerAccounts.Add(new LedgerAccount
        {
            Id = "acct-essentials-2", Name = "Second essentials", Bucket = "Essentials", Kind = LedgerAccountKind.Bank,
        });
        await context.SaveChangesAsync();
        var service = NewService(context, Today(2026, 7, 10));

        // The occurrence materialises against the payment's own account, so the second account is
        // one the schedule could point at later but this occurrence was never scheduled against.
        var result = await service.PayEarlyAsync("rec-1", new DateOnly(2026, 7, 15), accountId: "acct-essentials-2");

        Assert.Equal(PayEarlyStatus.InvalidAccount, result.Status);
        Assert.Equal("ledger_account_invalid", result.Code);
        Assert.Empty(context.Transactions);
    }

    [Fact]
    public async Task PayEarlyAsync_WritesTheOccurrenceSAccountOntoTheSettlementRow()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", startDate: "2026-01-01", dueDate: 15));
        await context.SaveChangesAsync();
        var service = NewService(context, Today(2026, 7, 10));

        var result = await service.PayEarlyAsync("rec-1", new DateOnly(2026, 7, 15), accountId: "acct-essentials");

        Assert.Equal(PayEarlyStatus.Success, result.Status);
        var settlement = Assert.Single(context.Transactions);
        Assert.Equal("acct-essentials", settlement.AccountId);
    }

    private static RecurringPaymentPayEarlyService NewService(AppDbContext context, FinancialClock clock)
    {
        // The explicit-account contract is seeded by the test helper below.
        if (context.LedgerAccounts.All(account => account.Id != "acct-essentials"))
        {
            context.LedgerAccounts.Add(new LedgerAccount
            {
                Id = "acct-essentials",
                Name = "Essentials account",
                Bucket = "Essentials",
                Kind = LedgerAccountKind.Bank,
            });
            context.SaveChanges();
        }
        var occurrenceService = new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        return new RecurringPaymentPayEarlyService(
            context,
            occurrenceService,
            new CycleBalanceService(context),
            clock);
    }

    private static FinancialClock Today(int year, int month, int day)
    {
        var configuration = TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC"));
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero));
        return new FinancialClock(configuration, timeProvider);
    }

    // A settlement writes the occurrence row in the same save as its transaction. Seeding only
    // the transaction models a state the app cannot reach, and leaves the bill still pending.
    private static RecurringPaymentOccurrence PaidOccurrence(
        RecurringPayment payment,
        DateOnly date,
        Transaction settledBy) => new()
    {
        Id = $"occ-{payment.Id}-{date:yyyyMMdd}",
        RecurringPaymentId = payment.Id,
        OccurrenceDate = date,
        Name = payment.Name,
        ScheduledAmount = Math.Abs(payment.Amount),
        Category = payment.Category,
        LedgerCategory = payment.LedgerCategory,
        PaymentMode = payment.PaymentMode,
        Status = RecurringOccurrenceStatus.Paid,
        PaidDate = TransactionDate.ToDateOnly(settledBy.Date),
        SettlementTransactionId = settledBy.Id
    };

    private static RecurringPayment NewPayment(
        string id,
        decimal amount = 100m,
        string category = "Bills",
        string ledgerCategory = "Essentials",
        string frequency = "Monthly",
        string startDate = "2026-01-01",
        int dueDate = 15,
        string? endDate = null,
        bool active = true,
        string paymentMode = RecurringPaymentMode.Manual)
    {
        return new RecurringPayment
        {
            PaymentMode = paymentMode,
            Id = id,
            Name = "Payment",
            Amount = amount,
            Frequency = frequency,
            Category = category,
            LedgerCategory = ledgerCategory,
            AccountId = "acct-essentials",
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
