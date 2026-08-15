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
            AdjustmentAccountId: "acct-main",
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
            AdjustmentAccountId: null,
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
    public async Task ReconcileAsync_PureRedistribution_CreatesAccountMovesWithoutBucketAdjustment()
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
            AdjustmentAccountId: null,
            Targets:
            [
                new("acct-1", "Bank", LedgerAccountKind.Bank, false, ExpectedCurrent: 100m, Target: 60m),
                new("acct-2", "Cash", LedgerAccountKind.Cash, false, ExpectedCurrent: 0m, Target: 40m)
            ]);

        var result = await service.ReconcileAsync(request);
        Assert.Equal(LedgerAccountMutationStatus.Success, result.Status);
        Assert.NotNull(result.Transactions);

        // Should have exactly 1 move transaction from acct-1 to acct-2 for 40m, no Adjustment
        var tx = Assert.Single(result.Transactions);
        Assert.Equal("AccountMove", tx.LedgerCategory);
        Assert.Equal("Transfer", tx.Category);
        Assert.Equal(40m, tx.Amount);
        Assert.Equal("acct-1", tx.AccountId);
        Assert.Equal("acct-2", tx.CounterAccountId);
    }

    [Fact]
    public async Task ReconcileAsync_GenuineTotalCorrection_CreatesAdjustmentAndMoves()
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
            AdjustmentAccountId: "acct-1",
            Targets:
            [
                new("acct-1", "Bank", LedgerAccountKind.Bank, false, ExpectedCurrent: 100m, Target: 120m),
                new("acct-2", "Cash", LedgerAccountKind.Cash, false, ExpectedCurrent: 0m, Target: 30m)
            ]);

        var result = await service.ReconcileAsync(request);
        Assert.Equal(LedgerAccountMutationStatus.Success, result.Status);
        Assert.NotNull(result.Transactions);

        // 1 Adjustment (+50 on acct-1) and 1 AccountMove (30 from acct-1 to acct-2)
        Assert.Equal(2, result.Transactions.Count);

        var adjustment = result.Transactions.Single(t => t.Category == "Adjustment");
        Assert.Equal("reconcile-op-correction-adjustment", adjustment.Id);
        Assert.Equal(50m, adjustment.Amount);
        Assert.Equal("acct-1", adjustment.AccountId);
        Assert.Equal("Essentials", adjustment.LedgerCategory);

        var move = result.Transactions.Single(t => t.Category == "Transfer");
        Assert.Equal("AccountMove", move.LedgerCategory);
        Assert.Equal(30m, move.Amount);
        Assert.Equal("acct-1", move.AccountId);
        Assert.Equal("acct-2", move.CounterAccountId);
    }
}
