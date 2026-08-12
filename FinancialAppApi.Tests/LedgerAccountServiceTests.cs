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
        bool isArchived = false) => new(
        id,
        name,
        "Essentials",
        LedgerAccountKind.Bank,
        isArchived,
        isDefault);

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
