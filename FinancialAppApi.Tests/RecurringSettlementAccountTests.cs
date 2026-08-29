using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

/// <summary>
/// Which ledger account a bill settlement is allowed to land in.
///
/// The occurrence's own frozen account is authoritative when it has one, so re-pointing a bill
/// never moves money already scheduled elsewhere. A bill authored before account attribution has
/// a blank account, which propagates onto its occurrences; that is "never assigned" rather than
/// "assigned to nothing", so there is no identity to preserve and only the bucket rule applies.
/// Demanding identity with a blank made those settlements permanently unsyncable: the refusal
/// asked for an account and then rejected every account the client could offer.
/// </summary>
public sealed class RecurringSettlementAccountTests
{
    private static readonly DateOnly DueDate = new(2026, 8, 1);

    [Fact]
    public async Task BillWithNoAccount_SettlesIntoAnOpenAccountInItsBucket()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedAsync(context, billAccountId: "");
        var service = CreateSettlement(context);

        var result = await service.SettleAsync(
            "bill-1", DueDate, RecurringOccurrenceStatus.Paid, new DateOnly(2026, 8, 1),
            clientKey: "settle", accountId: "acc-main");

        Assert.Equal(RecurringSettlementStatus.Success, result.Status);
        Assert.Equal("acc-main", await OccurrenceAccountAsync(context));
    }

    [Fact]
    public async Task BillWithNoAccount_StillRequiresOneToBeChosen()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedAsync(context, billAccountId: "");
        var service = CreateSettlement(context);

        var result = await service.SettleAsync(
            "bill-1", DueDate, RecurringOccurrenceStatus.Paid, new DateOnly(2026, 8, 1),
            clientKey: "settle", accountId: null);

        Assert.Equal(RecurringSettlementStatus.Invalid, result.Status);
        Assert.Equal("ledger_account_required", result.Code);
    }

    [Fact]
    public async Task BillWithNoAccount_StillRefusesAnAccountOutsideItsBucket()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedAsync(context, billAccountId: "");
        var service = CreateSettlement(context);

        var result = await service.SettleAsync(
            "bill-1", DueDate, RecurringOccurrenceStatus.Paid, new DateOnly(2026, 8, 1),
            clientKey: "settle", accountId: "acc-rewards");

        Assert.Equal(RecurringSettlementStatus.Invalid, result.Status);
        Assert.Equal("ledger_account_invalid", result.Code);
    }

    [Fact]
    public async Task BillWithNoAccount_KeepsThePartPaymentAndItsRemainderTogether()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedAsync(context, billAccountId: "");
        var service = CreateSettlement(context);

        var part = await service.SettleAsync(
            "bill-1", DueDate, RecurringOccurrenceStatus.Paid, new DateOnly(2026, 8, 1),
            clientKey: "part-a", accountId: "acc-main", requestedAmount: 40m);
        Assert.Equal(RecurringSettlementStatus.Success, part.Status);

        // The first part payment froze the account, so finishing the bill elsewhere is refused the
        // same way it would be for a bill that always had one.
        var elsewhere = await service.SettleAsync(
            "bill-1", DueDate, RecurringOccurrenceStatus.Paid, new DateOnly(2026, 8, 2),
            clientKey: "part-b", accountId: "acc-second", requestedAmount: 60m);

        Assert.Equal(RecurringSettlementStatus.Invalid, elsewhere.Status);
        Assert.Equal("ledger_account_invalid", elsewhere.Code);
    }

    [Fact]
    public async Task BillWithAnAssignedAccount_StillRefusesADifferentOpenAccount()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        await SeedAsync(context, billAccountId: "acc-main");
        var service = CreateSettlement(context);

        var result = await service.SettleAsync(
            "bill-1", DueDate, RecurringOccurrenceStatus.Paid, new DateOnly(2026, 8, 1),
            clientKey: "settle", accountId: "acc-second");

        Assert.Equal(RecurringSettlementStatus.Invalid, result.Status);
        Assert.Equal("ledger_account_invalid", result.Code);
    }

    private static async Task<string?> OccurrenceAccountAsync(AppDbContext context) =>
        (await context.RecurringPaymentOccurrences.AsNoTracking()
            .FirstAsync(o => o.RecurringPaymentId == "bill-1" && o.OccurrenceDate == DueDate)).AccountId;

    private static RecurringOccurrenceSettlementService CreateSettlement(AppDbContext context)
    {
        var clock = new FinancialClock(
            TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero)));
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

        return new RecurringOccurrenceSettlementService(context, ledger, persistence, clock);
    }

    private static async Task SeedAsync(AppDbContext context, string billAccountId)
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
        context.LedgerAccounts.AddRange(
            new LedgerAccount { Id = "acc-main", UserId = userId, Name = "Maybank", Bucket = "Essentials", Kind = "Bank" },
            new LedgerAccount { Id = "acc-second", UserId = userId, Name = "CIMB", Bucket = "Essentials", Kind = "Bank" },
            new LedgerAccount { Id = "acc-rewards", UserId = userId, Name = "Fun money", Bucket = "Rewards", Kind = "Bank" });
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
            AccountId = billAccountId,
            Active = true
        });

        await context.SaveChangesAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
