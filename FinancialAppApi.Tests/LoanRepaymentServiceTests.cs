using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Loans;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests;

public sealed class LoanRepaymentServiceTests
{
    [Fact]
    public async Task PreviewAdvanceCycles_CalculatesCycleBreakdown()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, payment, account) = await SeedLoanAsync(context);

        var service = CreateService(context);
        var preview = await service.PreviewAdvanceRepaymentAsync(loan.Id, cycles: 2);

        Assert.Equal(LoanRepaymentStatus.Success, preview.Status);
        Assert.NotNull(preview.Occurrences);
        Assert.Equal(2, preview.CyclesCount);
        Assert.Equal(200m, preview.TotalAmount);
        Assert.Equal(new DateOnly(2026, 1, 1), preview.Occurrences![0].OccurrenceDate);
        Assert.Equal(new DateOnly(2026, 2, 1), preview.Occurrences![1].OccurrenceDate);
    }

    [Fact]
    public async Task AdvanceCyclesRepayment_CreatesTransactionsAndPaidOccurrences()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, payment, account) = await SeedLoanAsync(context);

        var service = CreateService(context);
        var result = await service.AdvanceCyclesRepaymentAsync(
            loan.Id,
            cycles: 2,
            accountId: account.Id,
            clientKey: "key-1",
            postedAt: new DateTime(2026, 1, 1, 10, 0, 0));

        Assert.True(result.Status == LoanRepaymentStatus.Success, $"Expected Success but got {result.Status}: {result.Message} (Code: {result.Code})");
        Assert.Equal(2, result.Transactions!.Count);
        Assert.Equal("AdvanceCycles", result.Kind);
        Assert.NotNull(result.ActionId);

        // Check occurrences are marked as Paid
        var occurrences = await context.RecurringPaymentOccurrences
            .Where(o => o.RecurringPaymentId == payment.Id)
            .OrderBy(o => o.OccurrenceDate)
            .ToListAsync();

        Assert.Equal(2, occurrences.Count);
        Assert.All(occurrences, o => Assert.Equal(RecurringOccurrenceStatus.Paid, o.Status));

        // Check balance reduced
        Assert.Equal(800m, result.LoanView!.Replay.OutstandingBalance);
    }

    [Fact]
    public async Task AdvanceCyclesRepayment_AllowsAnyOpenAccountAndUsesItsBucketForTheLedgerEntry()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, _, _) = await SeedLoanAsync(context);
        var rewardsAccount = new LedgerAccount
        {
            Id = "acc-rewards",
            UserId = "test-user",
            Name = "Rewards wallet",
            Bucket = "Rewards",
            Kind = "Bank"
        };
        context.LedgerAccounts.Add(rewardsAccount);
        await context.SaveChangesAsync();

        var result = await CreateService(context).AdvanceCyclesRepaymentAsync(
            loan.Id, cycles: 1, accountId: rewardsAccount.Id, clientKey: "key-other-bucket");

        Assert.Equal(LoanRepaymentStatus.Success, result.Status);
        var transaction = Assert.Single(result.Transactions!);
        Assert.Equal(rewardsAccount.Id, transaction.AccountId);
        Assert.Equal("Rewards", transaction.LedgerCategory);
        Assert.Equal(loan.RecurringPaymentId, transaction.RecurringPaymentId);
        Assert.NotNull(transaction.RecurringOccurrenceDate);
    }

    [Fact]
    public async Task AdvanceCyclesRepayment_ReplaysTheSameActionAfterALostResponse()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, _, account) = await SeedLoanAsync(context);
        var service = CreateService(context);

        var first = await service.AdvanceCyclesRepaymentAsync(loan.Id, 2, account.Id, "retry-key");
        var retry = await service.AdvanceCyclesRepaymentAsync(loan.Id, 2, account.Id, "retry-key");

        Assert.Equal(LoanRepaymentStatus.Success, retry.Status);
        Assert.Equal(first.ActionId, retry.ActionId);
        Assert.Equal(2, await context.Transactions.CountAsync());
        Assert.Single(await context.LoanRepaymentActions.ToListAsync());
    }

    // A queued advance repayment can sit in the outbox while the schedule moves underneath it. The
    // preview fingerprint is what makes the retry safe: it commits the exact occurrence set and
    // amounts the user approved, so a changed schedule is refused instead of silently charging a
    // different payment.
    [Fact]
    public async Task AdvanceCyclesRepayment_AcceptsTheFingerprintFromItsOwnPreview()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, _, account) = await SeedLoanAsync(context);
        var service = CreateService(context);

        var preview = await service.PreviewAdvanceRepaymentAsync(loan.Id, cycles: 2);
        Assert.False(string.IsNullOrWhiteSpace(preview.PreviewFingerprint));

        var result = await service.AdvanceCyclesRepaymentAsync(
            loan.Id, 2, account.Id, "fingerprint-accepted", preview.PreviewFingerprint);

        Assert.Equal(LoanRepaymentStatus.Success, result.Status);
        Assert.Equal(2, result.Transactions!.Count);
    }

    [Fact]
    public async Task AdvanceCyclesRepayment_RejectsAPreviewThatNoLongerMatchesTheSchedule()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, _, account) = await SeedLoanAsync(context);
        var service = CreateService(context);

        var reviewed = await service.PreviewAdvanceRepaymentAsync(loan.Id, cycles: 2);

        // The schedule moves on while the approved repayment is still queued: one cycle is paid,
        // so "the next two" are no longer the two the user reviewed.
        var interleaved = await service.AdvanceCyclesRepaymentAsync(loan.Id, 1, account.Id, "interleaved");
        Assert.Equal(LoanRepaymentStatus.Success, interleaved.Status);
        var transactionsBefore = await context.Transactions.CountAsync();

        var stale = await service.AdvanceCyclesRepaymentAsync(
            loan.Id, 2, account.Id, "stale-preview", reviewed.PreviewFingerprint);

        Assert.Equal(LoanRepaymentStatus.Conflict, stale.Status);
        Assert.Equal("stale_repayment_preview", stale.Code);
        // Refused, not partially applied.
        Assert.Equal(transactionsBefore, await context.Transactions.CountAsync());
    }

    [Fact]
    public async Task AdvanceCyclesRepayment_ReplaysAFingerprintedActionExactlyOnce()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, _, account) = await SeedLoanAsync(context);
        var service = CreateService(context);
        var preview = await service.PreviewAdvanceRepaymentAsync(loan.Id, cycles: 2);

        var first = await service.AdvanceCyclesRepaymentAsync(
            loan.Id, 2, account.Id, "fingerprint-retry", preview.PreviewFingerprint);
        var retry = await service.AdvanceCyclesRepaymentAsync(
            loan.Id, 2, account.Id, "fingerprint-retry", preview.PreviewFingerprint);

        Assert.Equal(LoanRepaymentStatus.Success, retry.Status);
        Assert.Equal(first.ActionId, retry.ActionId);
        Assert.Equal(2, await context.Transactions.CountAsync());
        Assert.Single(await context.LoanRepaymentActions.ToListAsync());
    }

    // An older client sends no fingerprint at all. That must keep working rather than becoming an
    // unconfirmable repayment.
    [Fact]
    public async Task AdvanceCyclesRepayment_StillSucceedsWithoutAFingerprint()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, _, account) = await SeedLoanAsync(context);

        var result = await CreateService(context).AdvanceCyclesRepaymentAsync(
            loan.Id, 1, account.Id, "no-fingerprint", previewFingerprint: null);

        Assert.Equal(LoanRepaymentStatus.Success, result.Status);
        Assert.Single(result.Transactions!);
    }

    [Fact]
    public async Task FullSettlementRepayment_ClosesBillAndMarksFutureOccurrencesSettledByLoanPayoff()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, payment, account) = await SeedLoanAsync(context);

        var service = CreateService(context);
        var result = await service.FullSettlementRepaymentAsync(
            loan.Id,
            lenderQuoteAmount: 950m,
            accountId: account.Id,
            clientKey: "key-full-1",
            postedAt: new DateTime(2026, 1, 1, 10, 0, 0));

        Assert.Equal(LoanRepaymentStatus.Success, result.Status);
        Assert.Single(result.Transactions!);
        Assert.Equal(-950m, result.Transactions![0].Amount);

        // Bill is deactivated
        var updatedPayment = await context.RecurringPayments.FindAsync(payment.Id);
        Assert.False(updatedPayment!.Active);

        // Loan replay has 0 outstanding balance and payoff date set
        Assert.Equal(0m, result.LoanView!.Replay.OutstandingBalance);
        Assert.Equal(FinancialClock.Utc.Today, result.LoanView.Replay.PayoffDate);
    }

    [Fact]
    public async Task FullSettlementRepayment_AllowsAnyOpenAccountAndUsesItsBucketForTheLedgerEntry()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, _, _) = await SeedLoanAsync(context);
        var growthAccount = new LedgerAccount
        {
            Id = "acc-growth",
            UserId = "test-user",
            Name = "Growth account",
            Bucket = "Growth",
            Kind = "Bank"
        };
        context.LedgerAccounts.Add(growthAccount);
        await context.SaveChangesAsync();

        var result = await CreateService(context).FullSettlementRepaymentAsync(
            loan.Id, lenderQuoteAmount: 950m, accountId: growthAccount.Id, clientKey: "key-full-other-bucket");

        Assert.Equal(LoanRepaymentStatus.Success, result.Status);
        var transaction = Assert.Single(result.Transactions!);
        Assert.Equal(growthAccount.Id, transaction.AccountId);
        Assert.Equal("Growth", transaction.LedgerCategory);
        Assert.Equal(loan.RecurringPaymentId, transaction.RecurringPaymentId);
        Assert.NotNull(transaction.RecurringOccurrenceDate);
    }

    [Fact]
    public async Task FullSettlementRepayment_ReplaysTheSameActionAfterALostResponse()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, _, account) = await SeedLoanAsync(context);
        var service = CreateService(context);

        var first = await service.FullSettlementRepaymentAsync(loan.Id, 950m, account.Id, "settlement-retry");
        var retry = await service.FullSettlementRepaymentAsync(loan.Id, 950m, account.Id, "settlement-retry");

        Assert.Equal(LoanRepaymentStatus.Success, retry.Status);
        Assert.Equal(first.ActionId, retry.ActionId);
        Assert.Single(await context.Transactions.ToListAsync());
        Assert.Single(await context.LoanRepaymentActions.ToListAsync());
    }

    [Fact]
    public async Task RepaymentLedgerEntry_CanOnlyBeRemovedThroughUndo()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, _, account) = await SeedLoanAsync(context);
        var service = CreateService(context);
        var repayment = await service.AdvanceCyclesRepaymentAsync(loan.Id, 1, account.Id, "protected-ledger-row");

        var directDelete = await CreatePersistence(context).DeleteTransactionAsync(repayment.Transactions!.Single().Id);

        Assert.Equal(TransactionMutationStatus.Conflict, directDelete.Status);
        Assert.Contains("Undo repayment", directDelete.Message);
        Assert.Single(await context.Transactions.ToListAsync());

        var undo = await service.UndoRepaymentActionAsync(repayment.ActionId!);
        Assert.Equal(LoanRepaymentStatus.Success, undo.Status);
        Assert.Empty(await context.Transactions.ToListAsync());
    }

    [Fact]
    public async Task UndoRepayment_ReversesTransactionsAndRestoresBill()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, payment, account) = await SeedLoanAsync(context);

        var service = CreateService(context);
        var result = await service.FullSettlementRepaymentAsync(
            loan.Id,
            lenderQuoteAmount: 950m,
            accountId: account.Id,
            clientKey: "key-full-2",
            postedAt: new DateTime(2026, 1, 1, 10, 0, 0));

        Assert.Equal(LoanRepaymentStatus.Success, result.Status);

        // Undo
        var undoResult = await service.UndoRepaymentActionAsync(result.ActionId!);
        Assert.Equal(LoanRepaymentStatus.Success, undoResult.Status);

        // Bill is active again
        var restoredPayment = await context.RecurringPayments.FindAsync(payment.Id);
        Assert.True(restoredPayment!.Active);

        // Transaction is deleted
        var txCount = await context.Transactions.CountAsync();
        Assert.Equal(0, txCount);
    }

    [Fact]
    public async Task AutoDeductBill_RejectsAdvanceCyclesRepayment()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, payment, account) = await SeedLoanAsync(context, paymentMode: RecurringPaymentMode.AutoDeduct);

        var service = CreateService(context);
        var result = await service.AdvanceCyclesRepaymentAsync(
            loan.Id,
            cycles: 1,
            accountId: account.Id,
            clientKey: "key-ad",
            postedAt: new DateTime(2026, 1, 1, 10, 0, 0));

        Assert.Equal(LoanRepaymentStatus.Conflict, result.Status);
        Assert.Contains("automatically deducted", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdvancePreview_UsesTheSameManualOnlyAndCycleCapRulesAsExecution()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, _, _) = await SeedLoanAsync(context, paymentMode: RecurringPaymentMode.AutoDeduct);
        var service = CreateService(context);

        var automatic = await service.PreviewAdvanceRepaymentAsync(loan.Id, 1);
        var overLimit = await service.PreviewAdvanceRepaymentAsync(loan.Id, 61);

        Assert.Equal(LoanRepaymentStatus.Invalid, automatic.Status);
        Assert.Contains("automatically deducted", automatic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LoanRepaymentStatus.Invalid, overLimit.Status);
        Assert.Contains("between 1 and 60", overLimit.Message);
    }

    [Fact]
    public async Task FullSettlementRepayment_MarksEveryOpenOccurrenceSettledByLoanPayoff()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, payment, account) = await SeedLoanAsync(context);

        // Two already-materialised future instalments. Only rows dated from today forward, so the
        // arrears guard has nothing to refuse.
        var future = NextScheduleDate(1);
        var later = NextScheduleDate(2);
        AddOccurrence(context, payment, future, RecurringOccurrenceStatus.Pending);
        AddOccurrence(context, payment, later, RecurringOccurrenceStatus.Pending);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var result = await service.FullSettlementRepaymentAsync(
            loan.Id,
            lenderQuoteAmount: 950m,
            accountId: account.Id,
            clientKey: "key-sweep",
            postedAt: new DateTime(2026, 1, 1, 10, 0, 0));

        Assert.Equal(LoanRepaymentStatus.Success, result.Status);

        var occurrences = await context.RecurringPaymentOccurrences
            .Where(o => o.RecurringPaymentId == payment.Id)
            .ToListAsync();

        Assert.NotEmpty(occurrences);
        Assert.All(occurrences, o => Assert.Equal(RecurringOccurrenceStatus.SettledByLoanPayoff, o.Status));
    }

    [Fact]
    public async Task FullSettlementRepayment_RefusesWhileEarlierInstalmentsAreStillOpen()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, payment, account) = await SeedLoanAsync(context);

        var overdue = NextScheduleDate(-1);
        AddOccurrence(context, payment, overdue, RecurringOccurrenceStatus.Pending);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var result = await service.FullSettlementRepaymentAsync(
            loan.Id,
            lenderQuoteAmount: 950m,
            accountId: account.Id,
            clientKey: "key-arrears",
            postedAt: new DateTime(2026, 1, 1, 10, 0, 0));

        Assert.Equal(LoanRepaymentStatus.Conflict, result.Status);
        Assert.Contains(overdue.ToString("yyyy-MM-dd"), result.Message);

        // Nothing was written: the bill is still live and the arrears row is untouched.
        var unchanged = await context.RecurringPayments.FindAsync(payment.Id);
        Assert.True(unchanged!.Active);
        Assert.Equal(0, await context.LoanRepaymentActions.CountAsync());
        Assert.Equal(0, await context.Transactions.CountAsync());
    }

    [Fact]
    public async Task FullSettlementRepayment_RefusesASecondSettlement()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, payment, account) = await SeedLoanAsync(context);

        var service = CreateService(context);
        var first = await service.FullSettlementRepaymentAsync(
            loan.Id, 950m, account.Id, "key-once", new DateTime(2026, 1, 1, 10, 0, 0));
        Assert.Equal(LoanRepaymentStatus.Success, first.Status);

        var second = await service.FullSettlementRepaymentAsync(
            loan.Id, 950m, account.Id, "key-twice", new DateTime(2026, 1, 1, 10, 0, 0));

        Assert.Equal(LoanRepaymentStatus.Conflict, second.Status);
        Assert.Equal(1, await context.LoanRepaymentActions.CountAsync());
    }

    [Fact]
    public async Task FullSettlementRepayment_RejectsAutoDeductBill()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, _, account) = await SeedLoanAsync(context, paymentMode: RecurringPaymentMode.AutoDeduct);

        var service = CreateService(context);
        var result = await service.FullSettlementRepaymentAsync(
            loan.Id, 950m, account.Id, "key-ad-full", new DateTime(2026, 1, 1, 10, 0, 0));

        Assert.Equal(LoanRepaymentStatus.Conflict, result.Status);
        Assert.Contains("automatically deducted", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UndoRepayment_RestoresAPartiallyPaidOccurrenceRatherThanReopeningIt()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, payment, account) = await SeedLoanAsync(context);

        // A future instalment the user has already part paid: 40 of its 100.
        var future = NextScheduleDate(1);
        AddOccurrence(context, payment, future, RecurringOccurrenceStatus.PartiallyPaid);
        context.Transactions.Add(new Transaction
        {
            Id = "tx-part-1",
            UserId = "test-user",
            Date = TransactionDate.FromInputDate(FinancialClock.Utc.Today),
            PostedAt = DateTime.UtcNow,
            Description = payment.Name,
            Category = payment.Category,
            LedgerCategory = payment.LedgerCategory,
            Amount = -40m,
            RecurringPaymentId = payment.Id,
            RecurringOccurrenceDate = future,
            AccountId = account.Id
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var settled = await service.FullSettlementRepaymentAsync(
            loan.Id, 950m, account.Id, "key-partial", new DateTime(2026, 1, 1, 10, 0, 0));
        Assert.Equal(LoanRepaymentStatus.Success, settled.Status);

        var swallowed = await context.RecurringPaymentOccurrences
            .FirstAsync(o => o.RecurringPaymentId == payment.Id && o.OccurrenceDate == future);
        Assert.Equal(RecurringOccurrenceStatus.SettledByLoanPayoff, swallowed.Status);

        var undone = await service.UndoRepaymentActionAsync(settled.ActionId!);
        Assert.Equal(LoanRepaymentStatus.Success, undone.Status);

        // Its own part payment is still there, so it must come back partially paid — not Pending,
        // which would claim the whole instalment is owed again.
        var restored = await context.RecurringPaymentOccurrences
            .FirstAsync(o => o.RecurringPaymentId == payment.Id && o.OccurrenceDate == future);
        Assert.Equal(RecurringOccurrenceStatus.PartiallyPaid, restored.Status);
        Assert.True(await context.Transactions.AnyAsync(t => t.Id == "tx-part-1"));
    }

    [Fact]
    public async Task UndoRepayment_ReversesAnAdvanceRepayment()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var (loan, payment, account) = await SeedLoanAsync(context);

        var service = CreateService(context);
        var advance = await service.AdvanceCyclesRepaymentAsync(
            loan.Id, cycles: 2, accountId: account.Id, clientKey: "key-undo-adv",
            postedAt: new DateTime(2026, 1, 1, 10, 0, 0));
        Assert.Equal(LoanRepaymentStatus.Success, advance.Status);

        var undone = await service.UndoRepaymentActionAsync(advance.ActionId!);

        Assert.Equal(LoanRepaymentStatus.Success, undone.Status);
        Assert.Equal(0, await context.Transactions.CountAsync());
        Assert.Equal(0, await context.LoanRepaymentActions.CountAsync());

        var occurrences = await context.RecurringPaymentOccurrences
            .Where(o => o.RecurringPaymentId == payment.Id)
            .ToListAsync();
        Assert.All(occurrences, o => Assert.Equal(RecurringOccurrenceStatus.Pending, o.Status));
        Assert.Equal(1000m, undone.LoanView!.Replay.OutstandingBalance);
    }

    // The seeded bill is monthly on the 1st; offsets are whole months from the current cycle's 1st.
    private static DateOnly NextScheduleDate(int monthOffset)
    {
        var today = FinancialClock.Utc.Today;
        return new DateOnly(today.Year, today.Month, 1).AddMonths(monthOffset);
    }

    private static void AddOccurrence(
        AppDbContext context,
        RecurringPayment payment,
        DateOnly date,
        string status)
    {
        context.RecurringPaymentOccurrences.Add(new RecurringPaymentOccurrence
        {
            Id = $"occ-{payment.Id}-{date:yyyy-MM-dd}",
            UserId = payment.UserId,
            RecurringPaymentId = payment.Id,
            OccurrenceDate = date,
            Name = payment.Name,
            ScheduledAmount = payment.Amount,
            Category = payment.Category,
            LedgerCategory = payment.LedgerCategory,
            AccountId = payment.AccountId,
            PaymentMode = payment.PaymentMode,
            Status = status
        });
    }

    private static LoanRepaymentService CreateService(AppDbContext context)
    {
        var clock = FinancialClock.Utc;
        var loanService = new LoanService(context);
        var txPersistence = CreatePersistence(context);

        return new LoanRepaymentService(context, loanService, txPersistence, clock);
    }

    private static TransactionPersistenceService CreatePersistence(AppDbContext context)
    {
        var clock = FinancialClock.Utc;
        var occurrenceService = new RecurringOccurrenceService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RecurringOccurrenceService>.Instance);
        var cycleBalanceService = new CycleBalanceService(context);
        return new TransactionPersistenceService(
            context,
            cycleBalanceService,
            occurrenceService,
            new Services.Stability.StabilityRecoveryService(context, cycleBalanceService, clock));
    }

    private static async Task<(Loan Loan, RecurringPayment Payment, LedgerAccount Account)> SeedLoanAsync(
        AppDbContext context,
        string paymentMode = RecurringPaymentMode.Manual)
    {
        const string userId = "test-user";
        context.SetCurrentUser(userId);

        context.FinancialSettings.Add(new FinancialSetting
        {
            UserId = userId,
            CycleDay = 1
        });

        context.TransactionCategories.Add(new TransactionCategory
        {
            Id = "cat-transport",
            UserId = userId,
            Name = "Transport",
            Type = "Expense"
        });

        var account = new LedgerAccount
        {
            Id = "acc-main",
            UserId = userId,
            Name = "Maybank",
            Bucket = "Essentials",
            Kind = "Bank"
        };
        context.LedgerAccounts.Add(account);

        var payment = new RecurringPayment
        {
            Id = "bill-car",
            UserId = userId,
            Name = "Car Loan Bill",
            Amount = 100m,
            Frequency = "Monthly",
            Category = "Transport",
            LedgerCategory = "Essentials",
            StartDate = "2026-01-01",
            DueDate = 1,
            PaymentMode = paymentMode,
            AccountId = account.Id,
            Active = true
        };
        context.RecurringPayments.Add(payment);

        var loan = new Loan
        {
            Id = "loan-car",
            UserId = userId,
            Name = "Car Loan",
            RecurringPaymentId = payment.Id,
            OpeningPrincipal = 1000m,
            TrackingStartDate = new DateOnly(2026, 1, 1),
            ScheduleStatus = LoanScheduleStatus.Complete,
            ScheduleFrequency = "Monthly",
            ScheduleDueDay = 1,
            ScheduleStartDate = new DateOnly(2026, 1, 1),
            AnnualRatePercent = 0m,
            TermPeriods = 10,
            InterestMethod = LoanInterestMethod.ReducingBalance
        };
        context.Loans.Add(loan);

        await context.SaveChangesAsync();
        return (loan, payment, account);
    }
}
