using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class RecurringOccurrenceLedgerServiceTests
{
    [Fact]
    public async Task ScheduleEdit_PreservesDueHistoryAndRegeneratesOnlyFuturePending()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.TransactionCategories.Add(new TransactionCategory { Id = "cat-bills", Name = "Bills" });
        var payment = Payment();
        context.RecurringPayments.Add(payment);
        context.LedgerAccounts.Add(Account());
        await context.SaveChangesAsync();
        var clock = ClockAt(2026, 8, 9);
        var dates = new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        var ledger = new RecurringOccurrenceLedgerService(context, dates, clock);
        await ledger.GetRangeAsync([payment], new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 30));

        var service = new RecurringPaymentService(context, ledger, clock);
        var updated = Payment();
        updated.Name = "Edited bill";
        updated.DueDate = 10;
        updated.StartDate = "2026-08-10";
        await service.UpdateRecurringPaymentAsync(payment.Id, updated);

        var occurrences = await context.RecurringPaymentOccurrences.OrderBy(item => item.OccurrenceDate).ToListAsync();
        var preserved = Assert.Single(occurrences);
        Assert.Equal(new DateOnly(2026, 8, 5), preserved.OccurrenceDate);
        Assert.Equal("Original bill", preserved.Name);
        Assert.Equal(new DateOnly(2026, 8, 10), payment.OccurrenceTrackingStartDate);
    }

    [Fact]
    public async Task TaggedDiscard_BackfillKeepsHistoryWithoutInventingAmount()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.Transactions.Add(new Transaction
        {
            Id = "tx-discard", Date = new DateTime(2026, 7, 5), PostedAt = DateTime.UtcNow,
            Description = "[Discarded] Old bill", Category = "Bills", LedgerCategory = "Discarded",
            Amount = 0m, RecurringPaymentId = "deleted-payment",
            RecurringOccurrenceDate = new DateOnly(2026, 7, 5)
        });
        await context.SaveChangesAsync();
        var dates = new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        var ledger = new RecurringOccurrenceLedgerService(context, dates, ClockAt(2026, 8, 9));

        var rows = await ledger.GetRangeAsync([], new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        var occurrence = Assert.Single(rows);
        Assert.Equal(RecurringOccurrenceStatus.Discarded, occurrence.Status);
        Assert.Null(occurrence.ScheduledAmount);
    }

    [Fact]
    public async Task Resume_DoesNotBackfillCyclesThatElapsedWhilePaused()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var payment = Payment();
        context.RecurringPayments.Add(payment);
        context.LedgerAccounts.Add(Account());
        await context.SaveChangesAsync();
        var provider = new MutableTimeProvider(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
        var clock = new FinancialClock(TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")), provider);
        var dates = new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        var ledger = new RecurringOccurrenceLedgerService(context, dates, clock);
        var service = new RecurringPaymentService(context, ledger, clock);

        await service.ToggleActiveAsync(payment.Id, false);
        provider.UtcNow = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        await service.ToggleActiveAsync(payment.Id, true);

        var preserved = await context.RecurringPaymentOccurrences.ToListAsync();
        Assert.Equal([new DateOnly(2026, 8, 5)], preserved.Select(item => item.OccurrenceDate));
        var next = await ledger.GetNextPendingAsync(payment, clock.Today, includeFrom: false);
        Assert.Equal(new DateOnly(2026, 11, 5), next!.OccurrenceDate);
    }

    /// <summary>
    /// The paying account is frozen onto the occurrence with the rest of the snapshot, so
    /// re-pointing a bill moves only its future occurrences. One already materialised keeps the
    /// account it was scheduled against, exactly as it keeps its Name and ScheduledAmount.
    /// </summary>
    [Fact]
    public async Task ScheduleEdit_FreezesTheAccountOnMaterialisedOccurrencesAndMovesFutureOnes()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.TransactionCategories.Add(new TransactionCategory { Id = "cat-bills", Name = "Bills" });
        var payment = Payment();
        context.RecurringPayments.Add(payment);
        context.LedgerAccounts.Add(Account());
        context.LedgerAccounts.Add(new LedgerAccount
        {
            Id = "acct-essentials-2", Name = "Second essentials", Bucket = "Essentials", Kind = LedgerAccountKind.Bank,
        });
        await context.SaveChangesAsync();
        var clock = ClockAt(2026, 8, 9);
        var dates = new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        var ledger = new RecurringOccurrenceLedgerService(context, dates, clock);
        await ledger.GetRangeAsync([payment], new DateOnly(2026, 8, 1), new DateOnly(2026, 10, 31));
        await context.SaveChangesAsync();

        Assert.All(
            await context.RecurringPaymentOccurrences.ToListAsync(),
            occurrence => Assert.Equal("acct-essentials", occurrence.AccountId));

        var service = new RecurringPaymentService(context, ledger, clock);
        var updated = Payment();
        updated.AccountId = "acct-essentials-2";
        await service.UpdateRecurringPaymentAsync(payment.Id, updated);
        await context.SaveChangesAsync();

        // The 5 August row is already materialised and due, so it keeps the original account; the
        // future ones were reset and re-materialise against the new one.
        var preserved = await context.RecurringPaymentOccurrences
            .SingleAsync(occurrence => occurrence.OccurrenceDate == new DateOnly(2026, 8, 5));
        Assert.Equal("acct-essentials", preserved.AccountId);

        await ledger.GetRangeAsync([payment], new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 31));
        await context.SaveChangesAsync();
        var future = await context.RecurringPaymentOccurrences
            .Where(occurrence => occurrence.OccurrenceDate > new DateOnly(2026, 8, 9))
            .ToListAsync();
        Assert.NotEmpty(future);
        Assert.All(future, occurrence => Assert.Equal("acct-essentials-2", occurrence.AccountId));
    }

    [Fact]
    public async Task LegacyOccurrenceWithNullAccountId_PreservesStructure_AndFallsBackToParent()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var payment = Payment();
        context.RecurringPayments.Add(payment);
        context.RecurringPaymentOccurrences.Add(new RecurringPaymentOccurrence
        {
            Id = "occ-legacy",
            RecurringPaymentId = payment.Id,
            OccurrenceDate = new DateOnly(2026, 8, 5),
            Name = "Legacy Occurrence",
            Status = RecurringOccurrenceStatus.Pending,
            AccountId = null, // legacy occurrence
            UserId = TestHelpers.DefaultUserId,
        });
        await context.SaveChangesAsync();

        var clock = ClockAt(2026, 8, 9);
        var dates = new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        var ledger = new RecurringOccurrenceLedgerService(context, dates, clock);

        var rows = await ledger.GetRangeAsync([payment], new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));
        var occ = Assert.Single(rows);
        Assert.Equal("occ-legacy", occ.Id);
        Assert.Null(occ.AccountId); // persisted row remains null until re-settled or re-materialised
    }


    private static RecurringPayment Payment() => new()
    {
        Id = "rp-1", Name = "Original bill", Amount = 50m, Frequency = "Monthly",
        Category = "Bills", LedgerCategory = "Essentials", DueDate = 5,
        StartDate = "2026-08-05", NextDueDate = "2026-08-05", Active = true,
        AccountId = "acct-essentials",
        PaymentMode = RecurringPaymentMode.Manual,
        OccurrenceTrackingStartDate = new DateOnly(2026, 8, 1)
    };

    private static LedgerAccount Account() => new()
    {
        Id = "acct-essentials", Name = "Essentials account", Bucket = "Essentials", Kind = LedgerAccountKind.Bank,
    };

    private static FinancialClock ClockAt(int year, int month, int day) => new(
        TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")),
        new FixedTimeProvider(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero)));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class MutableTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = value;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
