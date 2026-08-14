using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Accounts;

namespace FinancialAppApi.Tests;

public class LedgerAccountServiceTests
{
    private static LedgerAccountService Service(AppDbContext context) => new(
        context,
        new LedgerAccountBalanceService(context),
        new CycleBalanceService(context));

    private static LedgerAccountMutation Mutation(
        string id,
        string name,
        bool isDefault = false,
        bool isArchived = false,
        decimal openingAmount = 0m) => new(
        id,
        name,
        "Essentials",
        LedgerAccountKind.Bank,
        isArchived,
        isDefault,
        openingAmount);

    [Fact]
    public async Task FirstAccountInABucketBecomesDefaultAndSecondDoesNot()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);

        var first = await service.CreateAsync(Mutation("acct-first", "Main bank"));
        var second = await service.CreateAsync(Mutation("acct-second", "Cash tin"));

        Assert.True(first.Account!.IsDefault);
        Assert.False(second.Account!.IsDefault);
    }

    [Fact]
    public async Task OpeningBalanceUsesUtcTransactionDate()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);

        var result = await service.CreateAsync(Mutation("acct-opening", "Main bank", openingAmount: 125m));

        Assert.Equal(LedgerAccountMutationStatus.Success, result.Status);
        var openingTransaction = context.Transactions.Single(transaction => transaction.Id == "acct-opening-opening");
        Assert.Equal(DateTimeKind.Utc, openingTransaction.Date.Kind);
    }

    [Fact]
    public async Task ReconcileAsync_RepeatedOperationReturnsTheOriginalAdjustment()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        await service.CreateAsync(Mutation("acct-main", "Main bank"));
        var request = new LedgerAccountReconcileRequest(
            "setup-1",
            "Essentials",
            0m,
            [new(
                "acct-main",
                "Main bank",
                LedgerAccountKind.Bank,
                true,
                false,
                0m,
                25m)]);

        var first = await service.ReconcileAsync(request);
        var repeated = await service.ReconcileAsync(request);

        Assert.Equal(LedgerAccountMutationStatus.Success, first.Status);
        Assert.Equal(LedgerAccountMutationStatus.Success, repeated.Status);
        var adjustment = Assert.Single(first.Transactions!);
        Assert.Equal("reconcile-setup-1-adjustment", adjustment.Id);
        Assert.Equal(adjustment, Assert.Single(repeated.Transactions!));
        Assert.Single(context.Transactions.Where(transaction => transaction.Id == adjustment.Id));
    }

    [Fact]
    public async Task ArchivingTheDefaultAssignsTheOldestRemainingOpenAccountInTheSameSave()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        await service.CreateAsync(Mutation("acct-first", "Main bank"));
        await service.CreateAsync(Mutation("acct-second", "Cash tin"));

        var result = await service.UpdateAsync(
            "acct-first",
            Mutation("acct-first", "Main bank", isArchived: true));

        Assert.Equal(LedgerAccountMutationStatus.Success, result.Status);
        var accounts = await service.GetAccountsAsync();
        Assert.True(accounts.Single(account => account.Id == "acct-first").IsArchived);
        Assert.True(accounts.Single(account => account.Id == "acct-second").IsDefault);
    }

    [Fact]
    public async Task EditingTheDefaultOfAMultiAccountBucketKeepsExactlyOneDefault()
    {
        // The old-bucket top-up excludes the edited account, so while that account was still the
        // bucket's default it promoted a second one and the save died on the unique
        // (UserId, Bucket) WHERE IsDefault index -- reached by something as ordinary as adding an
        // interest rate to the default account of a bucket that has two. The InMemory provider does
        // not enforce the index, so assert the state the index protects.
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        await service.CreateAsync(Mutation("acct-first", "Main bank"));
        await service.CreateAsync(Mutation("acct-second", "Cash tin"));

        var result = await service.UpdateAsync(
            "acct-first",
            Mutation("acct-first", "Main bank", isDefault: true) with
            {
                InterestEnabled = true,
                InterestRatePercent = 3.5m,
                InterestFrequency = LedgerAccountInterestFrequency.Monthly,
            });

        Assert.Equal(LedgerAccountMutationStatus.Success, result.Status);
        var accounts = await service.GetAccountsAsync();
        var defaults = accounts.Where(account => account.Bucket == "Essentials" && account.IsDefault).ToList();
        Assert.Equal("acct-first", Assert.Single(defaults).Id);
        var edited = accounts.Single(account => account.Id == "acct-first");
        Assert.True(edited.InterestEnabled);
        Assert.Equal(3.5m, edited.InterestRatePercent);
    }

    [Fact]
    public async Task EditingANonDefaultAccountLeavesTheExistingDefaultAlone()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        await service.CreateAsync(Mutation("acct-first", "Main bank"));
        await service.CreateAsync(Mutation("acct-second", "Cash tin"));

        var result = await service.UpdateAsync(
            "acct-second",
            Mutation("acct-second", "Cash tin") with { InterestEnabled = true, InterestRatePercent = 1m });

        Assert.Equal(LedgerAccountMutationStatus.Success, result.Status);
        var accounts = await service.GetAccountsAsync();
        var defaults = accounts.Where(account => account.Bucket == "Essentials" && account.IsDefault).ToList();
        Assert.Equal("acct-first", Assert.Single(defaults).Id);
    }

    [Fact]
    public async Task ArchivingTheLastOpenAccountReturnsConflict()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        await service.CreateAsync(Mutation("acct-only", "Main bank"));

        var result = await service.UpdateAsync(
            "acct-only",
            Mutation("acct-only", "Main bank", isArchived: true));

        Assert.Equal(LedgerAccountMutationStatus.Conflict, result.Status);
        Assert.Equal("Every bucket needs one open account. Add another before closing this one.", result.Message);
        Assert.False(context.LedgerAccounts.Single().IsArchived);
    }

    [Fact]
    public async Task DeleteWithLedgerActivityReturnsConflictAndLeavesTheAccount()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        await service.CreateAsync(Mutation("acct-used", "Main bank"));
        context.Transactions.Add(new Transaction
        {
            Id = "tx-used",
            Date = new DateTime(2026, 8, 1),
            Description = "Lunch",
            Category = "Food",
            LedgerCategory = "Essentials",
            Amount = -10m,
            AccountId = "acct-used",
        });
        await context.SaveChangesAsync();

        var result = await service.DeleteAsync("acct-used");

        Assert.Equal(LedgerAccountMutationStatus.Conflict, result.Status);
        Assert.NotNull(await context.LedgerAccounts.FindAsync("acct-used"));
    }
}
