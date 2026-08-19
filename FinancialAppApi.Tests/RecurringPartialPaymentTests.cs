using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.SavingsGoals;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

/// <summary>
/// A bill occurrence may now be settled by more than one transaction. There is no stored running
/// total, so every one of these asserts the same thing from a different angle: the status and the
/// still-owed figure are derived from the tagged transactions, and stay consistent as those are
/// added and removed.
/// </summary>
public sealed class RecurringPartialPaymentTests
{
    private static readonly DateOnly DueDate = new(2026, 8, 1);

    [Fact]
    public async Task PartialThenRemainder_AccumulatesToPaid()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedAsync(context);
        var service = CreateHarness(context).Settlement;

        var first = await service.SettleAsync(
            "bill-1", DueDate, RecurringOccurrenceStatus.Paid, new DateOnly(2026, 8, 1),
            clientKey: "part-a", accountId: "acc-main", requestedAmount: 30m);
        Assert.Equal(RecurringSettlementStatus.Success, first.Status);
        Assert.Equal(RecurringOccurrenceStatus.PartiallyPaid, await StatusAsync(context));

        var second = await service.SettleAsync(
            "bill-1", DueDate, RecurringOccurrenceStatus.Paid, new DateOnly(2026, 8, 2),
            clientKey: "part-b", accountId: "acc-main", requestedAmount: 70m);
        Assert.Equal(RecurringSettlementStatus.Success, second.Status);
        Assert.Equal(RecurringOccurrenceStatus.Paid, await StatusAsync(context));

        Assert.Equal(2, await context.Transactions.CountAsync());
        Assert.Equal(100m, await context.Transactions.SumAsync(t => Math.Abs(t.Amount)));
    }

    [Fact]
    public async Task TwoIdenticalPartPaymentsWithNoClientKey_BothLand()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedAsync(context);
        var service = CreateHarness(context).Settlement;

        // Same bill, same occurrence, same amount, no caller-supplied key. The derived transaction id
        // used to hash identically for both, so the second was mistaken for a retry of the first and
        // the money disappeared without any error.
        var first = await service.SettleAsync(
            "bill-1", DueDate, RecurringOccurrenceStatus.Paid, new DateOnly(2026, 8, 1), clientKey: null, accountId: "acc-main", requestedAmount: 50m);
        var second = await service.SettleAsync(
            "bill-1", DueDate, RecurringOccurrenceStatus.Paid, new DateOnly(2026, 8, 1), clientKey: null, accountId: "acc-main", requestedAmount: 50m);

        Assert.Equal(RecurringSettlementStatus.Success, first.Status);
        Assert.Equal(RecurringSettlementStatus.Success, second.Status);
        Assert.Equal(2, await context.Transactions.CountAsync());
        Assert.Equal(RecurringOccurrenceStatus.Paid, await StatusAsync(context));
    }

    [Fact]
    public async Task OverpayingTheRemainder_IsRefused()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedAsync(context);
        var service = CreateHarness(context).Settlement;

        await service.SettleAsync(
            "bill-1", DueDate, RecurringOccurrenceStatus.Paid, new DateOnly(2026, 8, 1),
            clientKey: "part-a", accountId: "acc-main", requestedAmount: 60m);

        var tooMuch = await service.SettleAsync(
            "bill-1", DueDate, RecurringOccurrenceStatus.Paid, new DateOnly(2026, 8, 2),
            clientKey: "part-b", accountId: "acc-main", requestedAmount: 60m);

        Assert.Equal(RecurringSettlementStatus.Conflict, tooMuch.Status);
        Assert.Contains("40", tooMuch.Message);
    }

    [Fact]
    public async Task DiscardingAPartlyPaidOccurrence_IsRefused()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedAsync(context);
        var service = CreateHarness(context).Settlement;

        await service.SettleAsync(
            "bill-1", DueDate, RecurringOccurrenceStatus.Paid, new DateOnly(2026, 8, 1),
            clientKey: "part-a", accountId: "acc-main", requestedAmount: 30m);

        var discarded = await service.SettleAsync(
            "bill-1", DueDate, RecurringOccurrenceStatus.Discarded, new DateOnly(2026, 8, 2), clientKey: "discard", accountId: "acc-main");

        Assert.Equal(RecurringSettlementStatus.Invalid, discarded.Status);
        Assert.Equal(RecurringOccurrenceStatus.PartiallyPaid, await StatusAsync(context));
    }

    [Fact]
    public async Task DeletingThePartPayment_ReturnsTheOccurrenceToPending()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedAsync(context);
        var harness = CreateHarness(context);

        var partial = await harness.Settlement.SettleAsync(
            "bill-1", DueDate, RecurringOccurrenceStatus.Paid, new DateOnly(2026, 8, 1),
            clientKey: "part-a", accountId: "acc-main", requestedAmount: 30m);
        Assert.Equal(RecurringOccurrenceStatus.PartiallyPaid, await StatusAsync(context));

        await harness.Persistence.DeleteTransactionsAsync([partial.Transaction!.Id]);

        Assert.Equal(RecurringOccurrenceStatus.Pending, await StatusAsync(context));
    }

    [Fact]
    public async Task PausingOrDeletingTheBill_IsBlockedWhileAPartPaymentIsOpen()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedAsync(context);
        var harness = CreateHarness(context);
        await harness.Settlement.SettleAsync(
            "bill-1", DueDate, RecurringOccurrenceStatus.Paid, new DateOnly(2026, 8, 1),
            clientKey: "part-a", accountId: "acc-main", requestedAmount: 30m);

        var payments = harness.Payments;

        var paused = await payments.ToggleActiveAsync("bill-1");
        Assert.Equal(ToggleRecurringPaymentStatus.HasPartialPayments, paused.Status);

        var deleted = await payments.DeleteRecurringPaymentAsync("bill-1");
        Assert.Equal(DeleteRecurringPaymentStatus.HasPartialPayments, deleted.Status);
    }

    [Fact]
    public void CommittedMoney_CountsOnlyWhatAPartlyPaidBillStillOwes()
    {
        var occurrence = new RecurringPaymentOccurrence
        {
            Id = "occ-1",
            RecurringPaymentId = "bill-1",
            OccurrenceDate = DueDate,
            Name = "Internet",
            ScheduledAmount = 100m,
            LedgerCategory = "Rewards",
            Status = RecurringOccurrenceStatus.PartiallyPaid
        };
        var paid = RecurringOccurrenceAmounts.PaidByOccurrence(
        [
            new Transaction { Id = "t1", RecurringPaymentId = "bill-1", RecurringOccurrenceDate = DueDate, Amount = -30m },
            // A discard marker is a decision, not money, so it must not reduce what is still owed.
            new Transaction { Id = "t2", RecurringPaymentId = "bill-1", RecurringOccurrenceDate = DueDate, Amount = 0m, LedgerCategory = "Discarded" },
        ]);

        Assert.Equal(70m, SavingsGoalPacing.PendingAmount([occurrence], "Rewards", paid));
    }

    [Fact]
    public void CommittedMoney_CountsNothingForAnOccurrenceSettledByALoanPayoff()
    {
        var occurrence = new RecurringPaymentOccurrence
        {
            Id = "occ-1",
            RecurringPaymentId = "bill-1",
            OccurrenceDate = DueDate,
            Name = "Car loan",
            ScheduledAmount = 100m,
            LedgerCategory = "Rewards",
            Status = RecurringOccurrenceStatus.SettledByLoanPayoff
        };

        Assert.Equal(0m, SavingsGoalPacing.PendingAmount([occurrence], "Rewards"));
    }

    private static async Task<string> StatusAsync(AppDbContext context) =>
        (await context.RecurringPaymentOccurrences.AsNoTracking()
            .FirstAsync(o => o.RecurringPaymentId == "bill-1" && o.OccurrenceDate == DueDate)).Status;

    private static FinancialClock Clock() => new(
        TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")),
        new FixedTimeProvider(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero)));

    // One ledger per context, exactly as DI scopes it. The ledger caches the occurrence entities it
    // materialises, so two instances over one context both try to Add the same row and EF rejects the
    // second for a duplicate key.
    private sealed record Harness(
        RecurringOccurrenceSettlementService Settlement,
        TransactionPersistenceService Persistence,
        RecurringPaymentService Payments);

    private static Harness CreateHarness(AppDbContext context)
    {
        var clock = Clock();
        var occurrenceService = new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        var ledger = new RecurringOccurrenceLedgerService(context, occurrenceService, clock);
        var cycleBalances = new CycleBalanceService(context);
        var persistence = new TransactionPersistenceService(
            context,
            cycleBalances,
            occurrenceService,
            new Services.Stability.StabilityRecoveryService(context, cycleBalances, clock),
            ledger,
            clock);

        return new Harness(
            new RecurringOccurrenceSettlementService(context, ledger, persistence, clock),
            persistence,
            new RecurringPaymentService(context, ledger, clock));
    }

    private static async Task SeedAsync(AppDbContext context)
    {
        const string userId = "test-user";
        context.SetCurrentUser(userId);

        context.FinancialSettings.Add(new FinancialSetting { UserId = userId, CycleDay = 1 });
        context.TransactionCategories.Add(new TransactionCategory
        {
            Id = "cat-bills",
            UserId = userId,
            Name = "Bills",
            Type = "Expense"
        });
        context.LedgerAccounts.Add(new LedgerAccount
        {
            Id = "acc-main",
            UserId = userId,
            Name = "Maybank",
            Bucket = "Essentials",
            Kind = "Bank"
        });
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "bill-1",
            UserId = userId,
            Name = "Internet",
            Amount = 100m,
            Frequency = "Monthly",
            Category = "Bills",
            LedgerCategory = "Essentials",
            StartDate = "2026-01-01",
            DueDate = 1,
            PaymentMode = RecurringPaymentMode.Manual,
            AccountId = "acc-main",
            Active = true
        });

        await context.SaveChangesAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
