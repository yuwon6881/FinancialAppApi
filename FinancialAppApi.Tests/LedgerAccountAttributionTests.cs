using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Accounts;

namespace FinancialAppApi.Tests;

public class LedgerAccountAttributionTests
{
    private static LedgerAccount Account(
        string id,
        string bucket,
        bool isDefault = false,
        bool isArchived = false) => new()
        {
            Id = id,
            Name = id,
            Bucket = bucket,
            Kind = LedgerAccountKind.Bank,
            IsDefault = isDefault,
            IsArchived = isArchived,
        };

    [Fact]
    public void PlainBucketUsesItsLegAndExplicitAccountEvenWhenThatAccountIsClosed()
    {
        var account = Account("closed", "Essentials", isArchived: true);
        var amount = LedgerAccountAttribution.GetAccountAmount(
            new Transaction { LedgerCategory = "Essentials", Amount = -80m, AccountId = account.Id },
            account,
            new Dictionary<string, LedgerAccount> { [account.Id] = account },
            new Dictionary<string, string>());

        Assert.Equal(-80m, amount);
    }

    [Fact]
    public void IncomeSplitUsesTheBucketShareNotTheRawSalaryAmount()
    {
        var account = Account("essentials", "Essentials", isDefault: true);
        var amount = LedgerAccountAttribution.GetAccountAmount(
            new Transaction { LedgerCategory = "IncomeSplit:50,25,15,10", Amount = 1000m },
            account,
            new Dictionary<string, LedgerAccount> { [account.Id] = account },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Essentials"] = account.Id });

        Assert.Equal(500m, amount);
    }

    [Fact]
    public void CrossBucketTransferCanUseAccountThenCounterAccountPlacement()
    {
        var source = Account("source", "Essentials", isDefault: true);
        var destination = Account("destination", "Rewards", isDefault: true);
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

        Assert.Equal(-120m, LedgerAccountAttribution.GetAccountAmount(transaction, source, all, new Dictionary<string, string>()));
        Assert.Equal(120m, LedgerAccountAttribution.GetAccountAmount(transaction, destination, all, new Dictionary<string, string>()));
    }

    [Fact]
    public void IncomeAndDiscardedRowsHaveNoAccountLeg()
    {
        var account = Account("essentials", "Essentials", isDefault: true);
        var all = new Dictionary<string, LedgerAccount> { [account.Id] = account };
        var defaults = new Dictionary<string, string> { ["Essentials"] = account.Id };

        Assert.Equal(0m, LedgerAccountAttribution.GetAccountAmount(
            new Transaction { LedgerCategory = "Income", Amount = 1000m }, account, all, defaults));
        Assert.Equal(0m, LedgerAccountAttribution.GetAccountAmount(
            new Transaction { LedgerCategory = "Discarded", Amount = -100m }, account, all, defaults));
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

        Assert.Equal(-40m, LedgerAccountAttribution.GetAccountAmount(transaction, source, all, new Dictionary<string, string>()));
        Assert.Equal(40m, LedgerAccountAttribution.GetAccountAmount(transaction, destination, all, new Dictionary<string, string>()));
        Assert.Equal(0m, CategoryAttributionService.GetCategoryAmount(transaction, "Essentials"));
    }

    [Fact]
    public void UntrackedLegFallsToTheLiveDefaultAndZeroAccountsRemainUntracked()
    {
        var account = Account("default", "Essentials", isDefault: true);
        var defaults = new Dictionary<string, string> { ["Essentials"] = account.Id };
        var all = new Dictionary<string, LedgerAccount> { [account.Id] = account };
        var transaction = new Transaction { LedgerCategory = "Essentials", Amount = -25m };

        Assert.Equal(-25m, LedgerAccountAttribution.GetAccountAmount(transaction, account, all, defaults));
        Assert.Null(LedgerAccountAttribution.GetPlacementAccountId(
            transaction,
            "Rewards",
            all,
            defaults));
    }
}
