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
    public void Describe_AccountBalanceCorrectionMovesMoneyWithoutPutBackLifecycle()
    {
        var movement = StabilityReloadLedger.Describe(
            Transaction("balance-correction", "Stability", 125m, isAccountBalanceAdjustment: true),
            stabilityAlloc: 0.15m);

        Assert.NotNull(movement);
        Assert.Equal(125m, movement.Value.Change);
        Assert.Equal(0m, movement.Value.Repayment);
        Assert.False(movement.Value.Marked);
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

    [Fact]
    public void Replay_RetainsPerWithdrawalOriginalAndRemainingAmountsInFifoOrder()
    {
        var state = StabilityReloadLedger.Replay(
            Opening(),
            openingBalance: 0m,
            target: 0m,
            [
                new ReloadMovement(new DateOnly(2026, 7, 1), -100m, 0m, true, "first"),
                new ReloadMovement(new DateOnly(2026, 7, 2), -80m, 0m, true, "second"),
                new ReloadMovement(new DateOnly(2026, 7, 3), 50m, 50m, false, "repayment"),
            ]);

        var obligations = state.Obligations!.ToDictionary(item => item.TransactionId);
        Assert.Equal((100m, 50m), (obligations["first"].OriginalAmount, obligations["first"].RemainingAmount));
        Assert.Equal((80m, 80m), (obligations["second"].OriginalAmount, obligations["second"].RemainingAmount));
        Assert.Equal(130m, state.Outstanding);
    }

    [Fact]
    public void Replay_ExactRepaymentMarksOnlyTheOldestWithdrawalComplete()
    {
        var state = StabilityReloadLedger.Replay(
            Opening(),
            0m,
            0m,
            [
                new ReloadMovement(new DateOnly(2026, 7, 1), -100m, 0m, true, "first"),
                new ReloadMovement(new DateOnly(2026, 7, 2), -80m, 0m, true, "second"),
                new ReloadMovement(new DateOnly(2026, 7, 3), 100m, 100m, false, "repayment"),
            ]);

        var obligations = state.Obligations!.ToDictionary(item => item.TransactionId);
        Assert.Equal(0m, obligations["first"].RemainingAmount);
        Assert.Equal(80m, obligations["second"].RemainingAmount);
        Assert.Equal(80m, state.Outstanding);
    }

    [Fact]
    public void Replay_RaisingTheTargetDoesNotReopenAnAttainedWithdrawal()
    {
        var state = StabilityReloadLedger.Replay(
            Opening(),
            openingBalance: 9000m,
            planPoints:
            [
                new ReloadPlanPoint(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), 10000m),
                new ReloadPlanPoint(new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc), 12000m),
            ],
            movements:
            [
                new ReloadMovement(new DateOnly(2026, 7, 1), -500m, 0m, true, "withdrawal",
                    new DateTime(2026, 7, 1, 1, 0, 0, DateTimeKind.Utc)),
                new ReloadMovement(new DateOnly(2026, 7, 2), 1500m, 0m, false, "salary",
                    new DateTime(2026, 7, 2, 1, 0, 0, DateTimeKind.Utc)),
            ]);

        Assert.Equal(0m, state.Outstanding);
        Assert.Equal(0m, state.Obligations!.Single(item => item.TransactionId == "withdrawal").RemainingAmount);
    }

    [Fact]
    public void Replay_LoweringTheTargetClearsOnlyWhatIsOutstandingAtThatRevision()
    {
        var state = StabilityReloadLedger.Replay(
            new ReloadState(200m, new DateOnly(2026, 6, 1), 0m, 0m),
            openingBalance: 9000m,
            planPoints:
            [new ReloadPlanPoint(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), 5000m)],
            movements:
            [new ReloadMovement(new DateOnly(2026, 7, 2), -5000m, 0m, true, "new-withdrawal",
                new DateTime(2026, 7, 2, 1, 0, 0, DateTimeKind.Utc))]);

        Assert.Equal(5000m, state.Outstanding);
        Assert.Equal(5000m, state.Obligations!.Single(item => item.TransactionId == "new-withdrawal").RemainingAmount);
    }

    /// <summary>
    /// A carried obligation must be discharged even when no carried date came with it. The queue
    /// used to be seeded only when the date was present, so the amount survived in a separate
    /// running total that repayments debited while the queue had nothing to discharge -- and the
    /// two never reconverged. Taken from a real ledger: 1,600.52 carried in, 1,048 of transfers
    /// and a 520 reimbursement putting money back, 871.77 marked across three drawdowns. The card
    /// reported being 904.29 short (more than had ever been marked) with 0.00 put back.
    /// </summary>
    [Fact]
    public void Replay_DischargesACarriedObligationThatHasNoCarriedDate()
    {
        var state = StabilityReloadLedger.Replay(
            new ReloadState(1600.52m, null, 0m, 0m),
            openingBalance: 5000m,
            target: 10000m,
            movements:
            [
                new ReloadMovement(new DateOnly(2026, 7, 28), 148m, 148m, false),
                new ReloadMovement(new DateOnly(2026, 7, 28), 900m, 900m, false),
                new ReloadMovement(new DateOnly(2026, 8, 6), -70m, 0m, true),
                new ReloadMovement(new DateOnly(2026, 8, 9), -125m, 0m, true),
                new ReloadMovement(new DateOnly(2026, 8, 9), -676.77m, 0m, true),
                new ReloadMovement(new DateOnly(2026, 8, 9), 520m, 520m, false),
            ]);

        // 1600.52 carried - 1048 - 520 repaid + 871.77 marked.
        Assert.Equal(904.29m, state.Outstanding);
        Assert.Equal(1568m, state.RepaidThisRun);
        Assert.Equal(871.77m, state.MarkedThisRun);
        // The carried entry is still at the head, so the window the card filters by reaches back
        // past this cycle rather than starting at the newest drawdown.
        Assert.Null(state.OldestOutstandingDate);
    }

    /// <summary>
    /// With nothing carried in, the same cycle owes exactly what it marked less what went back.
    /// This is the figure the user expects to read, and the desync above inflated it by the
    /// undischargeable carried amount.
    /// </summary>
    [Fact]
    public void Replay_OwesWhatWasMarkedLessWhatWentBack()
    {
        var state = StabilityReloadLedger.Replay(
            Opening(),
            openingBalance: 5000m,
            target: 10000m,
            movements:
            [
                new ReloadMovement(new DateOnly(2026, 7, 28), 148m, 148m, false),
                new ReloadMovement(new DateOnly(2026, 7, 28), 900m, 900m, false),
                new ReloadMovement(new DateOnly(2026, 8, 6), -70m, 0m, true),
                new ReloadMovement(new DateOnly(2026, 8, 9), -125m, 0m, true),
                new ReloadMovement(new DateOnly(2026, 8, 9), -676.77m, 0m, true),
                new ReloadMovement(new DateOnly(2026, 8, 9), 520m, 520m, false),
            ]);

        Assert.Equal(351.77m, state.Outstanding);
        Assert.Equal(520m, state.RepaidThisRun);
        // Transfers arriving before anything was marked repay nothing -- there was no obligation
        // for them to discharge yet.
        Assert.Equal(new DateOnly(2026, 8, 9), state.OldestOutstandingDate);
        // FIFO retired the 08-06 drawdown outright, so it is no longer outstanding -- but it is
        // still what this cycle marked and put back, and the reporting window has to reach it or
        // marked and repaid both lose the same 70.
        Assert.Equal(new DateOnly(2026, 8, 6), state.OldestMarkedThisRunDate);
    }

    private static ReloadState Opening() => new(0m, null, 0m, 0m);

    private static Transaction Transaction(
        string id,
        string ledgerCategory,
        decimal amount,
        decimal? stabilityRecoveryTopUp = null,
        bool isAccountBalanceAdjustment = false) => new()
    {
        Id = id,
        Date = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
        PostedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
        Description = id,
        Category = "Other",
        LedgerCategory = ledgerCategory,
        Amount = amount,
        StabilityRecoveryTopUpAmount = stabilityRecoveryTopUp,
        IsAccountBalanceAdjustment = isAccountBalanceAdjustment,
        StabilityReloadIntent = StabilityReloadIntent.Required
    };
}
