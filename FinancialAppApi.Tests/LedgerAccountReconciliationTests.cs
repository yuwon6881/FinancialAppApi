using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Accounts;

namespace FinancialAppApi.Tests;

public sealed class LedgerAccountReconciliationTests
{
    private static LedgerAccountService Service(AppDbContext context) => new(
        context,
        new LedgerAccountBalanceService(context),
        new CycleBalanceService(context));

    [Fact]
    public async Task ReconcileAsync_ReturnsConflict409_WhenBucketTotalChangedUnderneathPreview()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);

        var account = new LedgerAccount
        {
            Id = "acct-main",
            Name = "Main",
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank,
            UserId = TestHelpers.DefaultUserId,
        };
        context.LedgerAccounts.Add(account);
        context.Transactions.Add(new Transaction
        {
            Id = "tx-1",
            UserId = TestHelpers.DefaultUserId,
            Date = DateTime.UtcNow,
            PostedAt = DateTime.UtcNow,
            Description = "Initial",
            Category = "Adjustment",
            LedgerCategory = "Essentials",
            Amount = 100m,
            AccountId = "acct-main"
        });
        await context.SaveChangesAsync();

        // Request expects 50m bucket total, but actual is 100m => Stale preview 409
        var request = new LedgerAccountReconcileRequest(
            "op-stale-bucket",
            "Essentials",
            ExpectedBucketTotal: 50m,
            Targets:
            [
                new("acct-main", "Main", LedgerAccountKind.Bank, false, ExpectedCurrent: 50m, Target: 60m)
            ]);

        var result = await service.ReconcileAsync(request);
        Assert.Equal(LedgerAccountMutationStatus.Conflict, result.Status);
        Assert.Contains("The bucket changed while this setup was open", result.Message);
    }

    [Fact]
    public async Task ReconcileAsync_ReturnsConflict409_WhenAccountBalanceChangedUnderneathPreview()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);

        var a1 = new LedgerAccount { Id = "acct-1", Name = "Account 1", Bucket = "Essentials", Kind = LedgerAccountKind.Bank, UserId = TestHelpers.DefaultUserId };
        var a2 = new LedgerAccount { Id = "acct-2", Name = "Account 2", Bucket = "Essentials", Kind = LedgerAccountKind.Bank, UserId = TestHelpers.DefaultUserId };
        context.LedgerAccounts.AddRange(a1, a2);
        context.Transactions.AddRange(
            new Transaction { Id = "tx-1", UserId = TestHelpers.DefaultUserId, Date = DateTime.UtcNow, PostedAt = DateTime.UtcNow, Description = "1", Category = "Adjustment", LedgerCategory = "Essentials", Amount = 60m, AccountId = "acct-1" },
            new Transaction { Id = "tx-2", UserId = TestHelpers.DefaultUserId, Date = DateTime.UtcNow, PostedAt = DateTime.UtcNow, Description = "2", Category = "Adjustment", LedgerCategory = "Essentials", Amount = 40m, AccountId = "acct-2" }
        );
        await context.SaveChangesAsync();

        // Bucket total matches (100m), but individual expected currents differ (supposed to be 60 and 40, passed 70 and 30)
        var request = new LedgerAccountReconcileRequest(
            "op-stale-account",
            "Essentials",
            ExpectedBucketTotal: 100m,
            Targets:
            [
                new("acct-1", "Account 1", LedgerAccountKind.Bank, false, ExpectedCurrent: 70m, Target: 50m),
                new("acct-2", "Account 2", LedgerAccountKind.Bank, false, ExpectedCurrent: 30m, Target: 50m)
            ]);

        var result = await service.ReconcileAsync(request);
        Assert.Equal(LedgerAccountMutationStatus.Conflict, result.Status);
        Assert.Contains("An account balance changed while this setup was open", result.Message);
    }

    [Fact]
    public async Task ReconcileAsync_PureRedistribution_CreatesOneAdjustmentPerChangedAccount()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);

        var a1 = new LedgerAccount { Id = "acct-1", Name = "Bank", Bucket = "Essentials", Kind = LedgerAccountKind.Bank, UserId = TestHelpers.DefaultUserId };
        var a2 = new LedgerAccount { Id = "acct-2", Name = "Cash", Bucket = "Essentials", Kind = LedgerAccountKind.Cash, UserId = TestHelpers.DefaultUserId };
        context.LedgerAccounts.AddRange(a1, a2);
        context.Transactions.AddRange(
            new Transaction { Id = "tx-1", UserId = TestHelpers.DefaultUserId, Date = DateTime.UtcNow, PostedAt = DateTime.UtcNow, Description = "1", Category = "Adjustment", LedgerCategory = "Essentials", Amount = 100m, AccountId = "acct-1" },
            new Transaction { Id = "tx-2", UserId = TestHelpers.DefaultUserId, Date = DateTime.UtcNow, PostedAt = DateTime.UtcNow, Description = "2", Category = "Adjustment", LedgerCategory = "Essentials", Amount = 0m, AccountId = "acct-2" }
        );
        await context.SaveChangesAsync();

        var request = new LedgerAccountReconcileRequest(
            "op-redistribute",
            "Essentials",
            ExpectedBucketTotal: 100m,
            Targets:
            [
                new("acct-1", "Bank", LedgerAccountKind.Bank, false, ExpectedCurrent: 100m, Target: 60m),
                new("acct-2", "Cash", LedgerAccountKind.Cash, false, ExpectedCurrent: 0m, Target: 40m)
            ]);

        var result = await service.ReconcileAsync(request);
        Assert.Equal(LedgerAccountMutationStatus.Success, result.Status);
        Assert.NotNull(result.Transactions);

        var transactions = result.Transactions!;
        Assert.Equal(2, transactions.Count);
        Assert.Collection(
            transactions,
            tx =>
            {
                Assert.Equal("reconcile-op-redistribute-adjustment-0", tx.Id);
                Assert.Equal("Adjustment", tx.Category);
                Assert.Equal("Essentials", tx.LedgerCategory);
                Assert.Equal(-40m, tx.Amount);
                Assert.Equal("acct-1", tx.AccountId);
                Assert.True(tx.IsAccountBalanceAdjustment);
            },
            tx =>
            {
                Assert.Equal("reconcile-op-redistribute-adjustment-1", tx.Id);
                Assert.Equal("Adjustment", tx.Category);
                Assert.Equal("Essentials", tx.LedgerCategory);
                Assert.Equal(40m, tx.Amount);
                Assert.Equal("acct-2", tx.AccountId);
                Assert.True(tx.IsAccountBalanceAdjustment);
            });
    }

    [Fact]
    public async Task ReconcileAsync_GenuineTotalCorrection_CreatesOneAdjustmentPerChangedAccount()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);

        var a1 = new LedgerAccount { Id = "acct-1", Name = "Bank", Bucket = "Essentials", Kind = LedgerAccountKind.Bank, UserId = TestHelpers.DefaultUserId };
        context.LedgerAccounts.Add(a1);
        context.Transactions.Add(
            new Transaction { Id = "tx-1", UserId = TestHelpers.DefaultUserId, Date = DateTime.UtcNow, PostedAt = DateTime.UtcNow, Description = "1", Category = "Adjustment", LedgerCategory = "Essentials", Amount = 100m, AccountId = "acct-1" }
        );
        await context.SaveChangesAsync();

        // Target total is 150m (diff = +50m bucket adjustment on acct-1, plus creating new acct-2 with target 30m)
        var request = new LedgerAccountReconcileRequest(
            "op-correction",
            "Essentials",
            ExpectedBucketTotal: 100m,
            Targets:
            [
                new("acct-1", "Bank", LedgerAccountKind.Bank, false, ExpectedCurrent: 100m, Target: 120m),
                new("acct-2", "Cash", LedgerAccountKind.Cash, false, ExpectedCurrent: 0m, Target: 30m)
            ]);

        var result = await service.ReconcileAsync(request);
        Assert.Equal(LedgerAccountMutationStatus.Success, result.Status);
        Assert.NotNull(result.Transactions);

        // Each changed account receives its own visible adjustment row.
        Assert.Equal(2, result.Transactions.Count);

        var adjustment = result.Transactions.Single(t => t.AccountId == "acct-1");
        Assert.Equal("reconcile-op-correction-adjustment-0", adjustment.Id);
        Assert.Equal(20m, adjustment.Amount);
        Assert.Equal("acct-1", adjustment.AccountId);
        Assert.Equal("Essentials", adjustment.LedgerCategory);
        Assert.True(adjustment.IsAccountBalanceAdjustment);

        var second = result.Transactions.Single(t => t.AccountId == "acct-2");
        Assert.Equal("reconcile-op-correction-adjustment-1", second.Id);
        Assert.Equal(30m, second.Amount);
        Assert.Equal("acct-2", second.AccountId);
        Assert.True(second.IsAccountBalanceAdjustment);
    }

    [Fact]
    public async Task ReconcileAsync_OmittedKindPreservesExistingAccountKind()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        var account = new LedgerAccount
        {
            Id = "acct-cash",
            Name = "Cash",
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Cash,
            UserId = TestHelpers.DefaultUserId,
        };
        context.LedgerAccounts.Add(account);
        await context.SaveChangesAsync();

        var result = await service.ReconcileAsync(new LedgerAccountReconcileRequest(
            "op-preserve-kind",
            "Essentials",
            0m,
            [new("acct-cash", "Cash", null, false, 0m, 25m)]));

        Assert.Equal(LedgerAccountMutationStatus.Success, result.Status);
        Assert.Equal(LedgerAccountKind.Cash, context.LedgerAccounts.Single().Kind);
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotMixAnUndoOperationWithItsOriginal()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        var account = new LedgerAccount
        {
            Id = "acct-idempotency",
            Name = "Main",
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank,
            UserId = TestHelpers.DefaultUserId,
        };
        context.LedgerAccounts.Add(account);
        await context.SaveChangesAsync();

        var original = new LedgerAccountReconcileRequest(
            "K",
            "Essentials",
            0m,
            [new("acct-idempotency", "Main", LedgerAccountKind.Bank, false, 0m, 25m)]);
        var first = await service.ReconcileAsync(original);
        Assert.Equal(LedgerAccountMutationStatus.Success, first.Status);

        var undo = await service.ReconcileAsync(new LedgerAccountReconcileRequest(
            "K-undo",
            "Essentials",
            25m,
            [new("acct-idempotency", "Main", LedgerAccountKind.Bank, false, 25m, 0m)]));
        Assert.Equal(LedgerAccountMutationStatus.Success, undo.Status);

        var retry = await service.ReconcileAsync(original);
        Assert.Equal(LedgerAccountMutationStatus.Success, retry.Status);
        Assert.Collection(
            retry.Transactions!,
            transaction => Assert.Equal("reconcile-K-adjustment-0", transaction.Id));
    }

    [Fact]
    public async Task ReconcileAsync_ReturnsConflictWhenAccountIdentityChangedUnderneathPreview()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = Service(context);
        var account = new LedgerAccount
        {
            Id = "acct-stale-identity",
            Name = "Main",
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank,
            UserId = TestHelpers.DefaultUserId,
        };
        context.LedgerAccounts.Add(account);
        await context.SaveChangesAsync();

        account.Name = "Renamed elsewhere";
        await context.SaveChangesAsync();

        var result = await service.ReconcileAsync(new LedgerAccountReconcileRequest(
            "op-stale-identity",
            "Essentials",
            0m,
            [new(
                "acct-stale-identity",
                "Main",
                LedgerAccountKind.Bank,
                false,
                0m,
                10m,
                ExpectedName: "Main",
                ExpectedKind: LedgerAccountKind.Bank,
                ExpectedIsArchived: false)]));

        Assert.Equal(LedgerAccountMutationStatus.Conflict, result.Status);
        Assert.Contains("account changed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Renamed elsewhere", context.LedgerAccounts.Single().Name);
    }
}
