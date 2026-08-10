using FinancialAppApi.Models;
using FinancialAppApi.Services.Stability;

namespace FinancialAppApi.Tests;

public class StabilityReloadLedgerTests
{
    [Fact]
    public void Replay_UnmarkedWithdrawalRaisesNothing()
    {
        var state = StabilityReloadLedger.Replay(
            Opening(),
            openingBalance: 1000m,
            target: 10000m,
            [new ReloadMovement(new DateOnly(2026, 7, 1), -500m, 0m, Marked: false)]);

        Assert.Equal(0m, state.Outstanding);
    }

    [Fact]
    public void Replay_MarkedWithdrawalRaisesAnObligation()
    {
        var state = StabilityReloadLedger.Replay(
            Opening(), 1000m, 10000m,
            [new ReloadMovement(new DateOnly(2026, 7, 1), -500m, 0m, Marked: true)]);

        Assert.Equal(500m, state.Outstanding);
        Assert.Equal(new DateOnly(2026, 7, 1), state.OldestOutstandingDate);
        Assert.Equal(500m, state.MarkedThisRun);
    }

    [Fact]
    public void Describe_OrdinarySalaryShareRepaysNothing()
    {
        var movement = StabilityReloadLedger.Describe(
            Transaction("salary", "IncomeSplit:50,25,15,10", 1000m),
            stabilityAlloc: 0.15m);

        Assert.NotNull(movement);
        Assert.Equal(150m, movement.Value.Change);
        Assert.Equal(0m, movement.Value.Repayment);
    }

    [Fact]
    public void Describe_ExplicitTopUpRepaysTheAmountAboveTheNormalShare()
    {
        var movement = StabilityReloadLedger.Describe(
            Transaction("salary", "IncomeSplit:50,25,24,1", 1000m),
            stabilityAlloc: 0.15m);

        Assert.NotNull(movement);
        Assert.Equal(240m, movement.Value.Change);
        Assert.Equal(90m, movement.Value.Repayment);
    }

    [Fact]
    public void Describe_UsesStoredSalaryTopUpForHistoricalRows()
    {
        var movement = StabilityReloadLedger.Describe(
            Transaction("salary", "IncomeSplit:50,25,24,1", 1000m, stabilityRecoveryTopUp: 30m),
            stabilityAlloc: 0.15m);

        Assert.NotNull(movement);
        Assert.Equal(30m, movement.Value.Repayment);
    }

    [Fact]
    public void Describe_TransferIntoStabilityRepaysTheObligation()
    {
        var movement = StabilityReloadLedger.Describe(
            Transaction("transfer", "Transfer:Rewards->Stability", 300m),
            stabilityAlloc: 0.15m);

        Assert.Equal(300m, movement!.Value.Repayment);
    }

    [Fact]
    public void Describe_PositiveStabilityAdjustmentRepaysTheObligation()
    {
        var movement = StabilityReloadLedger.Describe(
            Transaction("adjustment", "Stability", 125m),
            stabilityAlloc: 0.15m);

        Assert.Equal(125m, movement!.Value.Repayment);
    }

    [Fact]
    public void Replay_ReachingTheTargetClearsTheQueue()
    {
        var state = StabilityReloadLedger.Replay(
            Opening(), 10000m, 10000m,
            [new ReloadMovement(new DateOnly(2026, 7, 1), -500m, 0m, Marked: true),
             new ReloadMovement(new DateOnly(2026, 7, 2), 500m, 0m, Marked: false)]);

        Assert.Equal(0m, state.Outstanding);
        Assert.Null(state.OldestOutstandingDate);
    }

    [Fact]
    public void Replay_WithNoTargetOnlyRepaymentClearsTheObligation()
    {
        var state = StabilityReloadLedger.Replay(
            new ReloadState(500m, new DateOnly(2026, 6, 1), 0m, 0m),
            openingBalance: 2000m,
            target: 0m,
            [new ReloadMovement(new DateOnly(2026, 7, 1), 500m, 500m, Marked: false)]);

        Assert.Equal(0m, state.Outstanding);
    }

    [Fact]
    public void Replay_OpeningBalanceAtLoweredTargetClearsWithoutAMovement()
    {
        var state = StabilityReloadLedger.Replay(
            new ReloadState(500m, new DateOnly(2026, 6, 1), 0m, 0m),
            openingBalance: 9500m,
            target: 5000m,
            []);

        Assert.Equal(0m, state.Outstanding);
        Assert.Null(state.OldestOutstandingDate);
    }

    [Fact]
    public void Replay_AttainmentResetsRepaymentBeforeALaterSameCycleDrawdown()
    {
        var state = StabilityReloadLedger.Replay(
            Opening(),
            openingBalance: 9000m,
            target: 10000m,
            [new ReloadMovement(new DateOnly(2026, 7, 1), -500m, 0m, Marked: true),
             new ReloadMovement(new DateOnly(2026, 7, 2), 1500m, 500m, Marked: false),
             new ReloadMovement(new DateOnly(2026, 7, 3), -200m, 0m, Marked: true)]);

        Assert.Equal(200m, state.Outstanding);
        Assert.Equal(0m, state.RepaidThisRun);
    }

    [Fact]
    public void Replay_RepaysOldestMarkedDrawdownFirst()
    {
        var state = StabilityReloadLedger.Replay(
            new ReloadState(100m, new DateOnly(2026, 5, 1), 0m, 0m),
            openingBalance: 0m,
            target: 0m,
            [new ReloadMovement(new DateOnly(2026, 6, 1), -50m, 0m, Marked: true),
             new ReloadMovement(new DateOnly(2026, 7, 1), 120m, 120m, Marked: false)]);

        Assert.Equal(30m, state.Outstanding);
        Assert.Equal(new DateOnly(2026, 6, 1), state.OldestOutstandingDate);
    }

    [Fact]
    public void Replay_WorkedScenario_OrdinarySalaryReachesTargetAndCancels()
    {
        var state = StabilityReloadLedger.Replay(
            Opening(), 10000m, 10000m,
            [new ReloadMovement(new DateOnly(2026, 6, 1), -500m, 0m, Marked: true),
             // The normal Stability share is 500, so it is not a repayment, but it restores the target.
             new ReloadMovement(new DateOnly(2026, 7, 1), 500m, 0m, Marked: false)]);

        Assert.Equal(0m, state.Outstanding);
    }

    [Fact]
    public void Replay_WorkedScenario_OrdinarySalaryBelowTargetDoesNotDischarge()
    {
        var state = StabilityReloadLedger.Replay(
            Opening(), 6000m, 10000m,
            [new ReloadMovement(new DateOnly(2026, 6, 1), -500m, 0m, Marked: true),
             // The balance rises to 6,300, but the full 800 is the ordinary salary share.
             new ReloadMovement(new DateOnly(2026, 7, 1), 800m, 0m, Marked: false)]);

        Assert.Equal(500m, state.Outstanding);
    }

    private static ReloadState Opening() => new(0m, null, 0m, 0m);

    private static Transaction Transaction(
        string id,
        string ledgerCategory,
        decimal amount,
        decimal? stabilityRecoveryTopUp = null) => new()
    {
        Id = id,
        Date = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
        PostedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
        Description = id,
        Category = "Other",
        LedgerCategory = ledgerCategory,
        Amount = amount,
        StabilityRecoveryTopUpAmount = stabilityRecoveryTopUp,
        StabilityReloadIntent = StabilityReloadIntent.Required
    };
}
