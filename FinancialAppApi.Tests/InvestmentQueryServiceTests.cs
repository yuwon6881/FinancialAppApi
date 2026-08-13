using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests;

public sealed class InvestmentQueryServiceTests
{
    [Fact]
    public async Task TransactionsAreFilteredCountedAndOrderedBeforePaging()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var account = new InvestmentAccount { Id = Guid.NewGuid(), Name = "Broker", BaseCurrency = "USD" };
        var otherAccount = new InvestmentAccount { Id = Guid.NewGuid(), Name = "Other", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument
        {
            Id = Guid.NewGuid(), Symbol = "AAA", Name = "Alpha", Currency = "USD", Type = "Stock"
        };
        context.AddRange(account, otherAccount, instrument);
        context.InvestmentTransactions.AddRange(
            Transaction(account.Id, instrument.Id, new DateOnly(2026, 8, 3), "Buy"),
            Transaction(account.Id, instrument.Id, new DateOnly(2026, 8, 2), "Buy"),
            Transaction(otherAccount.Id, instrument.Id, new DateOnly(2026, 8, 4), "Buy"));
        await context.SaveChangesAsync();

        var result = await new InvestmentQueryService(context).GetTransactionsAsync(
            account.Id, null, "Buy", null, null, 1, 10, CancellationToken.None);

        Assert.Equal(2, result.Total);
        Assert.Equal(new DateOnly(2026, 8, 3), result.Items[0].TradeDate);
        Assert.All(result.Items, item => Assert.Equal(account.Id, item.AccountId));
    }

    [Fact]
    public async Task AccountsPutOpenRowsFirstThenSortByName()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.InvestmentAccounts.AddRange(
            new InvestmentAccount { Id = Guid.NewGuid(), Name = "Zulu", BaseCurrency = "USD" },
            new InvestmentAccount { Id = Guid.NewGuid(), Name = "Alpha", BaseCurrency = "USD", IsArchived = true },
            new InvestmentAccount { Id = Guid.NewGuid(), Name = "Beta", BaseCurrency = "USD" });
        await context.SaveChangesAsync();

        var result = await new InvestmentQueryService(context).GetAccountsAsync(CancellationToken.None);

        Assert.Equal(["Beta", "Zulu", "Alpha"], result.Select(account => account.Name));
    }

    private static InvestmentTransaction Transaction(
        Guid accountId,
        Guid instrumentId,
        DateOnly tradeDate,
        string type) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        InstrumentId = instrumentId,
        TradeDate = tradeDate,
        Type = type,
        Units = 1,
        UnitPrice = 10,
        CreatedAt = tradeDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
    };
}
