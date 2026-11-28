using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Accounts;

namespace FinancialAppApi.Tests;

// A card's statement closes on a fixed day each month. What is still due on it is what is owed now
// less what was charged after the close: payments and refunds since then count against the
// statement, and new purchases wait for the next one.
public sealed class CardStatementTests
{
    private static LedgerAccountService Service(AppDbContext context) => new(
        context,
        new LedgerAccountBalanceService(context),
        new CycleBalanceService(context));

    private static LedgerAccountMutation Card(int? statementDay = 15, bool hasStatementDay = true, string kind = LedgerAccountKind.CreditCard) => new(
        "acct-visa", "Visa", "Essentials", kind, false, 0m, null, false, statementDay, hasStatementDay);

    private static LedgerAccountMutation Bank() => new("acct-bank", "Main bank", "Essentials", LedgerAccountKind.Bank, false);

    private static Transaction Row(string id, DateOnly date, decimal amount, string ledgerCategory = "Essentials", string? accountId = "acct-visa", string? counterAccountId = null) => new()
    {
        Id = id,
        UserId = TestHelpers.DefaultUserId,
        Date = TransactionDate.StartOfDate(date),
        Description = id,
        Category = ledgerCategory == "AccountMove" ? "Transfer" : "Food",
        LedgerCategory = ledgerCategory,
        Amount = amount,
        AccountId = accountId,
        CounterAccountId = counterAccountId,
    };

    [Theory]
    [InlineData(15, "2026-11-28", "2026-11-15")]
    [InlineData(15, "2026-11-15", "2026-10-15")] // issued at the end of its closing day
    [InlineData(15, "2026-11-10", "2026-10-15")]
    [InlineData(31, "2026-12-01", "2026-11-30")] // a short month closes on its last day
    [InlineData(31, "2026-03-15", "2026-02-28")]
    [InlineData(28, "2026-01-05", "2025-12-28")]
    public void LatestCloseIsTheMostRecentClosingDayBeforeToday(int day, string today, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected), CardStatementService.LastStatementDate(day, DateOnly.Parse(today)));
    }

    [Fact]
    public async Task OnlyChargesAfterTheCloseAreSetAsideAndPaymentsAreNot()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        await service.CreateAsync(Bank());
        await service.CreateAsync(Card(statementDay: 15));
        context.Transactions.AddRange(
            Row("before-close", new DateOnly(2026, 11, 3), -500m),
            Row("on-close-day", new DateOnly(2026, 11, 15), -100m),
            Row("after-close", new DateOnly(2026, 11, 20), -80m),
            Row("payment", new DateOnly(2026, 11, 22), 200m, "AccountMove", "acct-bank", "acct-visa"),
            Row("refund", new DateOnly(2026, 11, 23), 30m));
        await context.SaveChangesAsync();
        var accounts = await service.GetAccountsAsync();

        var statements = await new CardStatementService(context)
            .GetStatementsAsync(accounts, new DateOnly(2026, 11, 28), CancellationToken.None);

        var statement = Assert.Single(statements).Value;
        Assert.Equal(new DateOnly(2026, 11, 15), statement.StatementDate);
        Assert.Equal(80m, statement.ChargedSince);
        // Owed 450 now; 80 of it is next statement's, so 370 is still due (600 billed less 230 paid back).
        var owed = -(await service.GetBalancesAsync(accounts))["acct-visa"];
        Assert.Equal(450m, owed);
        Assert.Equal(370m, owed - statement.ChargedSince);
    }

    // A purchase made just before a close that the bank posted after it belongs to the next
    // statement, but only when it is marked so and only from the period that just closed.
    [Theory]
    [InlineData("2026-11-14", true, 60)] // the day before the latest close, posted after it
    [InlineData("2026-11-14", false, 0)] // the same purchase, posted before the close
    [InlineData("2026-10-15", true, 0)] // on the close before: that statement is long settled
    [InlineData("2026-10-10", true, 0)]
    public async Task AChargeMarkedForTheNextStatementCountsOnlyFromThePeriodThatJustClosed(
        string date, bool postsNextStatement, decimal expected)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        await service.CreateAsync(Bank());
        await service.CreateAsync(Card(statementDay: 15));
        var row = Row("late-post", DateOnly.Parse(date), -60m);
        row.PostsNextStatement = postsNextStatement;
        context.Transactions.Add(row);
        await context.SaveChangesAsync();

        var statements = await new CardStatementService(context)
            .GetStatementsAsync(await service.GetAccountsAsync(), new DateOnly(2026, 11, 28), CancellationToken.None);

        var statement = Assert.Single(statements).Value;
        Assert.Equal(new DateOnly(2026, 11, 15), statement.StatementDate);
        Assert.Equal(expected, statement.ChargedSince);
    }

    [Fact]
    public async Task ACardWithoutAStatementDayHasNoStatement()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        await service.CreateAsync(Bank());
        await service.CreateAsync(Card(statementDay: null, hasStatementDay: false));

        var statements = await new CardStatementService(context)
            .GetStatementsAsync(await service.GetAccountsAsync(), new DateOnly(2026, 11, 28), CancellationToken.None);

        Assert.Empty(statements);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public async Task AStatementDayMustBeADayOfTheMonth(int day)
    {
        await using var context = TestHelpers.NewInMemoryContext();

        var result = await Service(context).CreateAsync(Card(statementDay: day));

        Assert.Equal(LedgerAccountMutationStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task OnlyACardHasAStatementDay()
    {
        await using var context = TestHelpers.NewInMemoryContext();

        var result = await Service(context).CreateAsync(Card(statementDay: 15, kind: LedgerAccountKind.Bank) with { Id = "acct-x", Name = "X" });

        Assert.Equal(LedgerAccountMutationStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task AnUpdateWithoutAStatementDayKeepsItAndLeavingTheCardKindDropsIt()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        await service.CreateAsync(Bank());
        await service.CreateAsync(Card(statementDay: 15));

        await service.UpdateAsync("acct-visa", Card(statementDay: null, hasStatementDay: false));
        Assert.Equal(15, (await context.LedgerAccounts.FindAsync("acct-visa"))!.StatementDay);

        await service.UpdateAsync("acct-visa", Card(statementDay: 3));
        Assert.Equal(3, (await context.LedgerAccounts.FindAsync("acct-visa"))!.StatementDay);

        await service.UpdateAsync("acct-visa", Card(statementDay: null, hasStatementDay: false, kind: LedgerAccountKind.Bank));
        Assert.Null((await context.LedgerAccounts.FindAsync("acct-visa"))!.StatementDay);
    }
}
