using FinancialAppApi.Controllers;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests;

public sealed class InvestmentCashHistoryTests
{
    [Fact]
    public void SameDayCashEventsUseCreationTimeBeforeGuid()
    {
        var accountId = Guid.NewGuid();
        var depositId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var conversionId = Guid.Parse("00000000-0000-0000-0000-000000000000");

        var error = InvestmentsController.ValidateCashEvents([
            (new DateOnly(2026, 6, 1), new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc), depositId, accountId, "MYR", 1000m),
            (new DateOnly(2026, 6, 1), new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc), conversionId, accountId, "MYR", -1000m),
        ]);

        Assert.Null(error);
    }

    [Fact]
    public void TradeExecutionsMayTemporarilyOverdrawBeforeLaterSettlementFunding()
    {
        var accountId = Guid.NewGuid();
        var morning = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
        var events = new[]
        {
            new InvestmentCashEvent(new DateOnly(2026, 8, 1), morning, Guid.NewGuid(), accountId, "USD", 0.26m),
            new InvestmentCashEvent(new DateOnly(2026, 8, 2), morning, Guid.NewGuid(), accountId, "USD", -106.23m, true),
            new InvestmentCashEvent(new DateOnly(2026, 8, 2), morning.AddMinutes(1), Guid.NewGuid(), accountId, "USD", -11.35m, true),
            new InvestmentCashEvent(new DateOnly(2026, 8, 2), morning.AddMinutes(2), Guid.NewGuid(), accountId, "USD", -23.48m, true),
            new InvestmentCashEvent(new DateOnly(2026, 8, 3), morning, Guid.NewGuid(), accountId, "USD", 140.80m),
        };

        var replay = InvestmentHistoryValidationService.ReplayCashEvents(events, validateRestrictedDebits: true);

        Assert.Null(replay.Error);
        Assert.Equal(0m, replay.Balances[(accountId, "USD")]);
    }

    [Fact]
    public void ExplicitCashDebitStillCannotDeepenATemporaryNegativeBalance()
    {
        var accountId = Guid.NewGuid();
        var morning = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
        var events = new[]
        {
            new InvestmentCashEvent(new DateOnly(2026, 8, 1), morning, Guid.NewGuid(), accountId, "USD", -10m, true),
            new InvestmentCashEvent(new DateOnly(2026, 8, 2), morning, Guid.NewGuid(), accountId, "USD", -1m),
        };

        var replay = InvestmentHistoryValidationService.ReplayCashEvents(events, validateRestrictedDebits: true);

        Assert.Contains("Insufficient USD cash", replay.Error);
    }
}
