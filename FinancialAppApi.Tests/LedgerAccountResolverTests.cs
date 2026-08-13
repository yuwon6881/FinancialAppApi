using FinancialAppApi.Models;
using FinancialAppApi.Services.Accounts;

namespace FinancialAppApi.Tests;

public sealed class LedgerAccountResolverTests
{
    private static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Essentials"] = "essentials-default",
        ["Growth"] = "growth-default",
        ["Stability"] = "stability-default",
        ["Rewards"] = "rewards-default",
    };

    [Fact]
    public void ResolvesOrdinaryBucketAndBothTransferLegsWithoutReplacingExplicitIds()
    {
        var ordinary = new Transaction { LedgerCategory = "Essentials" };
        LedgerAccountResolver.ResolveMissing(ordinary, Defaults);
        Assert.Equal("essentials-default", ordinary.AccountId);

        var transfer = new Transaction { LedgerCategory = "Transfer:Essentials->Rewards" };
        LedgerAccountResolver.ResolveMissing(transfer, Defaults);
        Assert.Equal("essentials-default", transfer.AccountId);
        Assert.Equal("rewards-default", transfer.CounterAccountId);

        var explicitTransfer = new Transaction
        {
            LedgerCategory = "Transfer:Essentials->Rewards",
            AccountId = "bank-1",
            CounterAccountId = "wallet-1",
        };
        LedgerAccountResolver.ResolveMissing(explicitTransfer, Defaults);
        Assert.Equal("bank-1", explicitTransfer.AccountId);
        Assert.Equal("wallet-1", explicitTransfer.CounterAccountId);
    }

    [Fact]
    public void IncomeSplitKeepsOnlyItsDestinationAccountLeg()
    {
        var split = new Transaction { LedgerCategory = "Transfer:Income->Stability" };
        LedgerAccountResolver.ResolveMissing(split, Defaults);
        Assert.Equal("stability-default", split.AccountId);
        Assert.Null(split.CounterAccountId);
    }
}
