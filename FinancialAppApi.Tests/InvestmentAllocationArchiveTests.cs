using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests;

public sealed class InvestmentAllocationArchiveTests
{
    private static InvestmentInstrumentSetupDto Setup(Guid id, string symbol, string? sleeve, int order, bool archived = false) =>
        new(id, symbol, symbol, "ETF", null, null, null, "USD", symbol, null, false, archived, sleeve, order,
            true, true, null, null);

    private static InvestmentHoldingDto Holding(Guid instrumentId, string symbol, decimal? valueApp) =>
        new(Guid.NewGuid(), "Broker", instrumentId, symbol, symbol, "ETF", "USD", 1, 1, valueApp is null ? null : 1, 1,
            valueApp, 0, 0, 0, 0, valueApp, 0, 0, 0, 0, 0, DateOnly.FromDateTime(DateTime.UtcNow), DateTime.UtcNow,
            false, 1, DateOnly.FromDateTime(DateTime.UtcNow), DateTime.UtcNow, "Manual FX", "test", null);

    private static InvestmentCashBalanceDto Cash(decimal amount) => new(Guid.NewGuid(), "Broker", "USD", amount, amount);

    [Fact]
    public async Task Allocation_LeavesArchivedInvestmentsOutOfClassification()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.InvestmentPlans.Add(InvestmentAllocationServiceTests.MigratedPlan());
        await context.SaveChangesAsync();
        var live = Guid.NewGuid();
        var archived = Guid.NewGuid();

        var allocation = await new InvestmentAllocationService(context).BuildAsync(
            "USD", [], [Setup(live, "VTI", "USEquity", 1), Setup(archived, "OLD", "Bonds", 0, archived: true)],
            [], [], true, CancellationToken.None);

        Assert.Equal([live], allocation.Assignments.Select(value => value.InstrumentId));
    }

    [Fact]
    public async Task AllocationOrder_AcceptsTheActiveInvestmentsAndKeepsArchivedOnesAfterThem()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var first = new InvestmentInstrument { Symbol = "AAA", Name = "AAA", AllocationOrder = 0 };
        var second = new InvestmentInstrument { Symbol = "BBB", Name = "BBB", AllocationOrder = 1 };
        var archived = new InvestmentInstrument { Symbol = "OLD", Name = "OLD", AllocationOrder = 2, IsArchived = true };
        context.InvestmentInstruments.AddRange(first, second, archived);
        await context.SaveChangesAsync();
        var service = new InvestmentAllocationService(context);

        Assert.True(await service.UpdateAllocationOrderAsync([second.Id, first.Id], CancellationToken.None));
        Assert.Equal((0, 1, 2), (second.AllocationOrder, first.AllocationOrder, archived.AllocationOrder));

        // A change queued before the archive still names it, and still applies.
        Assert.True(await service.UpdateAllocationOrderAsync([archived.Id, first.Id, second.Id], CancellationToken.None));
        Assert.Equal((0, 1, 2), (archived.AllocationOrder, first.AllocationOrder, second.AllocationOrder));

        Assert.False(await service.UpdateAllocationOrderAsync([first.Id], CancellationToken.None));
        Assert.False(await service.UpdateAllocationOrderAsync([first.Id, second.Id, Guid.NewGuid()], CancellationToken.None));
    }

    [Fact]
    public async Task Allocation_RoundsCashAndBasketValuesOnEveryPath()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.InvestmentPlans.Add(InvestmentAllocationServiceTests.MigratedPlan());
        await context.SaveChangesAsync();
        var service = new InvestmentAllocationService(context);
        var sorted = Guid.NewGuid();
        var loose = Guid.NewGuid();
        var setups = new[] { Setup(sorted, "VTI", "USEquity", 0), Setup(loose, "LOOSE", null, 1) };

        var empty = await service.BuildAsync("USD", [], setups, [Cash(10.005m)], [], true, CancellationToken.None);
        var incomplete = await service.BuildAsync(
            "USD", [Holding(sorted, "VTI", 100.126m), Holding(loose, "LOOSE", 5m)], setups, [Cash(10.005m)], [], true,
            CancellationToken.None);
        var zero = await service.BuildAsync(
            "USD", [Holding(sorted, "VTI", 0m)], setups, [Cash(10.005m)], [], true, CancellationToken.None);

        Assert.Equal(10.01m, empty.AvailableCash);
        Assert.Equal("Incomplete", incomplete.Status);
        Assert.Equal(10.01m, incomplete.AvailableCash);
        Assert.Equal(100.13m, incomplete.Sleeves.Single(value => value.Sleeve == "USEquity").Value);
        Assert.Equal("NotStarted", zero.Status);
        Assert.Equal(10.01m, zero.AvailableCash);
    }
}
