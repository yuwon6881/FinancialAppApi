using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Accounts;

namespace FinancialAppApi.Tests;

public class LedgerAccountAttributionTests
{
    private static LedgerAccount Account(
        string id,
        string bucket,
        bool isArchived = false) => new()
        {
            Id = id,
            Name = id,
            Bucket = bucket,
            Kind = LedgerAccountKind.Bank,
            IsArchived = isArchived,
        };

    [Fact]
    public void PlainBucketUsesItsLegAndExplicitAccountEvenWhenThatAccountIsClosed()
    {
        var account = Account("closed", "Essentials", isArchived: true);
        var amount = LedgerAccountAttribution.GetAccountAmount(
            new Transaction { LedgerCategory = "Essentials", Amount = -80m, AccountId = account.Id },
            account,
            new Dictionary<string, LedgerAccount> { [account.Id] = account });

        Assert.Equal(-80m, amount);
    }

    [Fact]
    public void IncomeSplitUsesTheBucketShareNotTheRawSalaryAmount()
    {
        var account = Account("essentials", "Essentials");
        var amount = LedgerAccountAttribution.GetAccountAmount(
            new Transaction { LedgerCategory = "IncomeSplit:50,25,15,10", Amount = 1000m, AccountId = account.Id },
            account,
            new Dictionary<string, LedgerAccount> { [account.Id] = account });

        Assert.Equal(500m, amount);
    }

    [Fact]
    public void CrossBucketTransferCanUseAccountThenCounterAccountPlacement()
    {
        var source = Account("source", "Essentials");
        var destination = Account("destination", "Rewards");
        var all = new Dictionary<string, LedgerAccount>
        {
            [source.Id] = source,
            [destination.Id] = destination,
        };
        var transaction = new Transaction
        {
            LedgerCategory = "Transfer:Essentials->Rewards",
            Amount = 120m,
            AccountId = source.Id,
            CounterAccountId = destination.Id,
        };

        Assert.Equal(-120m, LedgerAccountAttribution.GetAccountAmount(transaction, source, all));
        Assert.Equal(120m, LedgerAccountAttribution.GetAccountAmount(transaction, destination, all));
    }

    [Fact]
    public void IncomeAndDiscardedRowsHaveNoAccountLeg()
    {
        var account = Account("essentials", "Essentials");
        var all = new Dictionary<string, LedgerAccount> { [account.Id] = account };

        Assert.Equal(0m, LedgerAccountAttribution.GetAccountAmount(
            new Transaction { LedgerCategory = "Income", Amount = 1000m }, account, all));
        Assert.Equal(0m, LedgerAccountAttribution.GetAccountAmount(
            new Transaction { LedgerCategory = "Discarded", Amount = -100m }, account, all));
    }

    [Fact]
    public void AccountMoveMovesAbsoluteMoneyWithoutChangingItsBucket()
    {
        var source = Account("source", "Essentials");
        var destination = Account("destination", "Essentials");
        var all = new Dictionary<string, LedgerAccount>
        {
            [source.Id] = source,
            [destination.Id] = destination,
        };
        var transaction = new Transaction
        {
            Category = "Transfer",
            LedgerCategory = "AccountMove",
            Amount = 40m,
            AccountId = source.Id,
            CounterAccountId = destination.Id,
        };

        Assert.Equal(-40m, LedgerAccountAttribution.GetAccountAmount(transaction, source, all));
        Assert.Equal(40m, LedgerAccountAttribution.GetAccountAmount(transaction, destination, all));
        Assert.Equal(0m, CategoryAttributionService.GetCategoryAmount(transaction, "Essentials"));
    }

    [Fact]
    public void SelfReferentialAccountMoveHasNoNetAccountEffect()
    {
        var account = Account("same", "Essentials");
        var transaction = new Transaction
        {
            Category = "Transfer",
            LedgerCategory = "AccountMove",
            Amount = 40m,
            AccountId = account.Id,
            CounterAccountId = account.Id,
        };

        Assert.Equal(0m, LedgerAccountAttribution.GetAccountAmount(
            transaction,
            account,
            new Dictionary<string, LedgerAccount> { [account.Id] = account }));
    }

    [Fact]
    public void UntrackedLegIsNotAssignedToAnAccount()
    {
        var account = Account("account", "Essentials");
        var all = new Dictionary<string, LedgerAccount> { [account.Id] = account };
        var transaction = new Transaction { LedgerCategory = "Essentials", Amount = -25m };

        Assert.Equal(0m, LedgerAccountAttribution.GetAccountAmount(transaction, account, all));
        Assert.Null(LedgerAccountAttribution.GetPlacementAccountId(
            transaction,
            "Rewards",
            all));
    }
}
