using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Accounts;

namespace FinancialAppApi.Tests;

// A credit card is a ledger account whose balance may sit below zero: the negative balance is what
// is owed. A purchase on it is spending on the day it happens; paying the card is a structural move
// that never counts as spending again. The limit is borrowing room and never money.
public sealed class LedgerAccountCreditCardTests
{
    private static LedgerAccountService Service(AppDbContext context) => new(
        context,
        new LedgerAccountBalanceService(context),
        new CycleBalanceService(context));

    private static LedgerAccountMutation Card(
        string id = "acct-visa",
        string bucket = "Essentials",
        decimal openingAmount = 0m,
        decimal? creditLimit = null,
        bool hasCreditLimit = false,
        bool isArchived = false) => new(
        id,
        "Visa",
        bucket,
        LedgerAccountKind.CreditCard,
        isArchived,
        openingAmount,
        creditLimit,
        hasCreditLimit);

    private static LedgerAccountMutation Bank(string id = "acct-bank", decimal openingAmount = 0m) => new(
        id,
        "Main bank",
        "Essentials",
        LedgerAccountKind.Bank,
        false,
        openingAmount);

    [Theory]
    [InlineData("Stability")]
    [InlineData("Growth")]
    public async Task CreditCardsCannotSitInReserveOrSavingsBuckets(string bucket)
    {
        await using var context = TestHelpers.NewInMemoryContext();

        var result = await Service(context).CreateAsync(Card(bucket: bucket));

        Assert.Equal(LedgerAccountMutationStatus.Invalid, result.Status);
        Assert.Empty(context.LedgerAccounts);
    }

    [Fact]
    public async Task CardOpensWithWhatIsOwedAndTheBucketCountsTheDebt()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        await service.CreateAsync(Bank(openingAmount: 2000m));

        var result = await service.CreateAsync(Card(openingAmount: -300m, creditLimit: 5000m, hasCreditLimit: true));

        Assert.Equal(LedgerAccountMutationStatus.Success, result.Status);
        Assert.Equal(5000m, result.Account!.CreditLimit);
        var accounts = await service.GetAccountsAsync();
        var balances = await service.GetBalancesAsync(accounts);
        Assert.Equal(-300m, balances["acct-visa"]);
        Assert.Equal(1700m, accounts.Sum(account => balances.GetValueOrDefault(account.Id)));
    }

    [Fact]
    public async Task OnlyACardMayOpenBelowZero()
    {
        await using var context = TestHelpers.NewInMemoryContext();

        var result = await Service(context).CreateAsync(Bank(openingAmount: -1m));

        Assert.Equal(LedgerAccountMutationStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ALimitBelongsOnlyToACardAndMustBePositive()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);

        var onBank = await service.CreateAsync(Bank() with { CreditLimit = 1000m, HasCreditLimit = true });
        var zero = await service.CreateAsync(Card(creditLimit: 0m, hasCreditLimit: true));

        Assert.Equal(LedgerAccountMutationStatus.Invalid, onBank.Status);
        Assert.Equal(LedgerAccountMutationStatus.Invalid, zero.Status);
    }

    [Fact]
    public async Task AnUpdateWithoutALimitKeepsTheStoredOne()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        await service.CreateAsync(Card(creditLimit: 5000m, hasCreditLimit: true));

        var renamed = await service.UpdateAsync("acct-visa", Card() with { Name = "Visa Platinum" });

        Assert.Equal(LedgerAccountMutationStatus.Success, renamed.Status);
        Assert.Equal(5000m, renamed.Account!.CreditLimit);
    }

    [Fact]
    public async Task ANullLimitClearsItAndLeavingTheCardKindDropsIt()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        await service.CreateAsync(Bank());
        await service.CreateAsync(Card(creditLimit: 5000m, hasCreditLimit: true));

        var cleared = await service.UpdateAsync("acct-visa", Card(creditLimit: null, hasCreditLimit: true));
        Assert.Null(cleared.Account!.CreditLimit);

        await service.UpdateAsync("acct-visa", Card(creditLimit: 5000m, hasCreditLimit: true));
        var asDebit = await service.UpdateAsync("acct-visa", Card() with { Kind = LedgerAccountKind.Card });

        Assert.Equal(LedgerAccountMutationStatus.Success, asDebit.Status);
        Assert.Null(asDebit.Account!.CreditLimit);
    }

    [Fact]
    public async Task ACardStillOwingCannotBeClosed()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        await service.CreateAsync(Bank(openingAmount: 100m));
        await service.CreateAsync(Card(openingAmount: -40m));

        var result = await service.UpdateAsync("acct-visa", Card(isArchived: true));

        Assert.Equal(LedgerAccountMutationStatus.Conflict, result.Status);
        Assert.Equal("Pay off this card before closing it.", result.Message);
        Assert.False((await context.LedgerAccounts.FindAsync("acct-visa"))!.IsArchived);
    }

    [Fact]
    public async Task ReconcileSetsACardBelowZeroAndAppliesItsLimit()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        await service.CreateAsync(Bank(openingAmount: 1000m));
        await service.CreateAsync(Card());

        var result = await service.ReconcileAsync(new LedgerAccountReconcileRequest(
            "card-owed",
            "Essentials",
            1000m,
            [
                new("acct-bank", "Main bank", LedgerAccountKind.Bank, false, 1000m, 1000m),
                new("acct-visa", "Visa", LedgerAccountKind.CreditCard, false, 0m, -250m,
                    CreditLimit: 3000m, HasCreditLimit: true),
            ]));

        Assert.Equal(LedgerAccountMutationStatus.Success, result.Status);
        var card = result.Accounts!.Single(account => account.Id == "acct-visa");
        Assert.Equal(3000m, card.CreditLimit);
        var balances = await service.GetBalancesAsync(result.Accounts!);
        Assert.Equal(-250m, balances["acct-visa"]);
    }

    [Fact]
    public async Task ReconcileRejectsANegativeTargetOnANonCardAccountEvenWhenTheKindIsOmitted()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        await service.CreateAsync(Bank(openingAmount: 100m));

        var result = await service.ReconcileAsync(new LedgerAccountReconcileRequest(
            "bank-negative",
            "Essentials",
            100m,
            [new("acct-bank", "Main bank", null, false, 100m, -5m)]));

        Assert.Equal(LedgerAccountMutationStatus.Invalid, result.Status);
        Assert.Equal(100m, (await service.GetBalancesAsync(await service.GetAccountsAsync()))["acct-bank"]);
    }

    [Fact]
    public async Task CardPurchaseIsSpendingAndPayingTheCardIsAStructuralMoveWithNoBucketEffect()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        await service.CreateAsync(Bank(openingAmount: 1000m));
        await service.CreateAsync(Card());
        var purchase = new Transaction
        {
            Id = "tx-groceries",
            UserId = TestHelpers.DefaultUserId,
            Date = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc),
            Description = "Groceries",
            Category = "Food",
            LedgerCategory = "Essentials",
            Amount = -80m,
            AccountId = "acct-visa",
        };
        var payment = new Transaction
        {
            Id = "tx-pay-visa",
            UserId = TestHelpers.DefaultUserId,
            Date = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc),
            Description = "Pay Visa",
            Category = "Transfer",
            LedgerCategory = "AccountMove",
            Amount = 80m,
            AccountId = "acct-bank",
            CounterAccountId = "acct-visa",
        };
        context.Transactions.Add(purchase);
        await context.SaveChangesAsync();

        var accounts = await service.GetAccountsAsync();
        var afterPurchase = await service.GetBalancesAsync(accounts);
        Assert.Equal(-80m, afterPurchase["acct-visa"]);
        Assert.Equal(920m, afterPurchase.Values.Sum());
        Assert.True(TransactionReportSemantics.IsReportableOutflow(purchase));

        context.Transactions.Add(payment);
        await context.SaveChangesAsync();
        var afterPayment = await service.GetBalancesAsync(accounts);
        Assert.Equal(0m, afterPayment["acct-visa"]);
        Assert.Equal(920m, afterPayment["acct-bank"]);
        Assert.Equal(920m, afterPayment.Values.Sum());
        Assert.False(TransactionReportSemantics.IsReportableCashMovement(payment));
    }

    [Fact]
    public void AutoDeductOnACardIsMeasuredAgainstAvailableCreditAndSkippedWhenTheLimitIsUnknown()
    {
        var today = new DateOnly(2026, 8, 15);
        var limited = new LedgerAccount
        {
            Id = "acct-limited", Name = "Visa", Bucket = "Essentials",
            Kind = LedgerAccountKind.CreditCard, CreditLimit = 1000m,
        };
        var unknown = new LedgerAccount
        {
            Id = "acct-unknown", Name = "Amex", Bucket = "Essentials",
            Kind = LedgerAccountKind.CreditCard,
        };
        var recurring = new List<RecurringPayment>
        {
            new() { Id = "rec-gym", Name = "Gym", Amount = 300m, PaymentMode = RecurringPaymentMode.AutoDeduct, AccountId = limited.Id, LedgerCategory = "Essentials" },
            new() { Id = "rec-stream", Name = "Streaming", Amount = 20m, PaymentMode = RecurringPaymentMode.AutoDeduct, AccountId = unknown.Id, LedgerCategory = "Essentials" },
        };
        var occurrences = new List<RecurringPaymentOccurrence>
        {
            new() { Id = "occ-gym", RecurringPaymentId = "rec-gym", Name = "Gym", ScheduledAmount = 300m, OccurrenceDate = today.AddDays(1), Status = RecurringOccurrenceStatus.Pending, AccountId = limited.Id },
            new() { Id = "occ-stream", RecurringPaymentId = "rec-stream", Name = "Streaming", ScheduledAmount = 20m, OccurrenceDate = today.AddDays(1), Status = RecurringOccurrenceStatus.Pending, AccountId = unknown.Id },
        };

        var shortfalls = RecurringAccountShortfallEvaluator.EvaluateShortfalls(
            occurrences,
            recurring,
            [limited, unknown],
            new Dictionary<string, decimal> { [limited.Id] = -800m, [unknown.Id] = -50m },
            today);

        dynamic result = Assert.Single(shortfalls);
        Assert.Equal("rec-gym", (string)result.recurringPaymentId);
        Assert.True((bool)result.isCreditCard);
        Assert.Equal(200m, ObfuscationHelper.Deobfuscate((string)result.accountBalance));
        Assert.Equal(100m, ObfuscationHelper.Deobfuscate((string)result.shortfall));
    }
}
