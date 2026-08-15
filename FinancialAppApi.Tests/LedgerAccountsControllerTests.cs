using System.Text.Json;
using FinancialAppApi.Controllers;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Accounts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FinancialAppApi.Tests;

public sealed class LedgerAccountsControllerTests
{
    private static LedgerAccountsController CreateController(AppDbContext context)
    {
        var balanceService = new LedgerAccountBalanceService(context);
        var cycleBalanceService = new CycleBalanceService(context);
        var accountService = new LedgerAccountService(context, balanceService, cycleBalanceService, FinancialClock.Utc);
        return new LedgerAccountsController(accountService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static JsonElement ToJsonElement(object value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task GetAccounts_ReturnsPeriodAwareBalances_ForCurrentUser()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = CreateController(context);

        var account = new LedgerAccount
        {
            Id = "acct-main",
            Name = "Main Account",
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank,
            UserId = TestHelpers.DefaultUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.LedgerAccounts.Add(account);
        context.Transactions.Add(new Transaction
        {
            Id = "tx-1",
            UserId = TestHelpers.DefaultUserId,
            Date = DateTime.UtcNow,
            PostedAt = DateTime.UtcNow,
            Description = "Groceries",
            Category = "Food",
            LedgerCategory = "Essentials",
            Amount = 150m,
            AccountId = "acct-main"
        });
        await context.SaveChangesAsync();

        var result = await controller.GetAccounts();
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var accounts = Assert.IsAssignableFrom<IReadOnlyList<LedgerAccountDto>>(okResult.Value);

        var dto = Assert.Single(accounts);
        Assert.Equal("acct-main", dto.Id);
        Assert.Equal("Main Account", dto.Name);
        Assert.Equal("Essentials", dto.Bucket);
        Assert.True(ObfuscationHelper.TryDeobfuscate(dto.Remaining, out var balance));
        Assert.Equal(150m, balance);
    }

    [Fact]
    public async Task PostAccount_CreatesAccount_AndReturnsCreatedAtAction()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = CreateController(context);

        var mutation = new LedgerAccountMutationDto
        {
            Id = "acct-new",
            Name = "Emergency Savings",
            Bucket = "Stability",
            Kind = LedgerAccountKind.Bank,
            OpeningAmount = ToJsonElement(500m),
            InterestEnabled = true,
            InterestRatePercent = 4.5m,
            InterestFrequency = LedgerAccountInterestFrequency.Monthly
        };

        var result = await controller.PostAccount(mutation);
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<LedgerAccountDto>(createdResult.Value);

        Assert.Equal("acct-new", dto.Id);
        Assert.Equal("Emergency Savings", dto.Name);
        Assert.Equal("Stability", dto.Bucket);
        Assert.True(dto.InterestEnabled);
        Assert.Equal(4.5m, dto.InterestRatePercent);

        var saved = await context.LedgerAccounts.FindAsync("acct-new");
        Assert.NotNull(saved);
        Assert.Equal(TestHelpers.DefaultUserId, saved.UserId);
        Assert.Equal("Emergency Savings", saved.Name);
    }

    [Fact]
    public async Task PutAccount_UpdatesAccount_AndReturnsOk()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = CreateController(context);

        var account = new LedgerAccount
        {
            Id = "acct-edit",
            Name = "Old Name",
            Bucket = "Growth",
            Kind = LedgerAccountKind.Bank,
            UserId = TestHelpers.DefaultUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var account2 = new LedgerAccount
        {
            Id = "acct-growth-2",
            Name = "Second Growth",
            Bucket = "Growth",
            Kind = LedgerAccountKind.Bank,
            UserId = TestHelpers.DefaultUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.LedgerAccounts.AddRange(account, account2);
        await context.SaveChangesAsync();

        var mutation = new LedgerAccountMutationDto
        {
            Id = "acct-edit",
            Name = "New Broker Name",
            Bucket = "Growth",
            Kind = LedgerAccountKind.Other,
            IsArchived = true
        };

        var result = await controller.PutAccount("acct-edit", mutation);
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<LedgerAccountDto>(okResult.Value);

        Assert.Equal("New Broker Name", dto.Name);
        Assert.Equal(LedgerAccountKind.Other, dto.Kind);
        Assert.True(dto.IsArchived);

        var updated = await context.LedgerAccounts.FindAsync("acct-edit");
        Assert.NotNull(updated);
        Assert.Equal("New Broker Name", updated.Name);
        Assert.True(updated.IsArchived);
    }

    [Fact]
    public async Task PutAccount_ReturnsNotFound_WhenAccountDoesNotExist()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = CreateController(context);

        var mutation = new LedgerAccountMutationDto
        {
            Id = "acct-missing",
            Name = "Missing",
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank
        };

        var result = await controller.PutAccount("acct-missing", mutation);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task DeleteAccount_DeletesUnreferencedAccount_AndReturnsNoContent()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = CreateController(context);

        var account = new LedgerAccount
        {
            Id = "acct-del",
            Name = "To Delete",
            Bucket = "Rewards",
            Kind = LedgerAccountKind.EWallet,
            UserId = TestHelpers.DefaultUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var account2 = new LedgerAccount
        {
            Id = "acct-rewards-keep",
            Name = "Keep Rewards",
            Bucket = "Rewards",
            Kind = LedgerAccountKind.EWallet,
            UserId = TestHelpers.DefaultUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.LedgerAccounts.AddRange(account, account2);
        await context.SaveChangesAsync();

        var result = await controller.DeleteAccount("acct-del");
        Assert.IsType<NoContentResult>(result);

        Assert.Null(await context.LedgerAccounts.FindAsync("acct-del"));
    }

    [Fact]
    public async Task DeleteAccount_ReturnsConflict_WhenAccountHasTransactions()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = CreateController(context);

        var account = new LedgerAccount
        {
            Id = "acct-active",
            Name = "Active Account",
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank,
            UserId = TestHelpers.DefaultUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.LedgerAccounts.Add(account);
        context.Transactions.Add(new Transaction
        {
            Id = "tx-active",
            UserId = TestHelpers.DefaultUserId,
            Date = DateTime.UtcNow,
            PostedAt = DateTime.UtcNow,
            Description = "Activity",
            Category = "Food",
            LedgerCategory = "Essentials",
            Amount = -25m,
            AccountId = "acct-active"
        });
        await context.SaveChangesAsync();

        var result = await controller.DeleteAccount("acct-active");
        var conflict = Assert.IsType<ConflictObjectResult>(result);
        dynamic val = conflict.Value!;
        Assert.True((int)val.activityCount > 0);
    }

    [Fact]
    public async Task MultiUserIsolation_ProtectsAccountsAcrossUsers()
    {
        await using var context = TestHelpers.NewInMemoryContext(currentUserId: null);
        context.AppUsers.AddRange(
            new AppUser { Id = "user-alice", Username = "alice", PasswordHash = "hash" },
            new AppUser { Id = "user-bob", Username = "bob", PasswordHash = "hash" });
        context.LedgerAccounts.AddRange(
            new LedgerAccount { Id = "alice-acct", UserId = "user-alice", Name = "Alice Checking", Bucket = "Essentials", Kind = LedgerAccountKind.Bank },
            new LedgerAccount { Id = "bob-acct", UserId = "user-bob", Name = "Bob Checking", Bucket = "Essentials", Kind = LedgerAccountKind.Bank });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        // Alice context
        context.SetCurrentUser("user-alice");
        var aliceController = CreateController(context);

        var aliceGet = await aliceController.GetAccounts();
        var aliceAccounts = Assert.IsAssignableFrom<IReadOnlyList<LedgerAccountDto>>(
            Assert.IsType<OkObjectResult>(aliceGet.Result).Value);
        Assert.Single(aliceAccounts);
        Assert.Equal("alice-acct", aliceAccounts[0].Id);

        // Alice cannot update Bob's account (returns NotFound because of tenancy filter)
        var updateResult = await aliceController.PutAccount("bob-acct", new LedgerAccountMutationDto
        {
            Id = "bob-acct",
            Name = "Hacked Name",
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank
        });
        Assert.IsType<NotFoundResult>(updateResult.Result);

        // Alice cannot delete Bob's account
        var deleteResult = await aliceController.DeleteAccount("bob-acct");
        Assert.IsType<NotFoundResult>(deleteResult);
    }
}
