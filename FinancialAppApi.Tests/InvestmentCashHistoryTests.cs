using FinancialAppApi.Controllers;

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
}