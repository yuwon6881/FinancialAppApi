using FinancialAppApi.Controllers;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FinancialAppApi.Tests;

public sealed class InvestmentAllocationControllerTests
{
    [Fact]
    public async Task PlanAndClassificationRoutesValidateAndPersist()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var first = new InvestmentInstrument { Symbol = "AAA", Name = "First", Type = "ETF", Currency = "USD" };
        var second = new InvestmentInstrument { Symbol = "BBB", Name = "Second", Type = "ETF", Currency = "USD" };
        context.InvestmentInstruments.AddRange(first, second);
        await context.SaveChangesAsync();
        var controller = NewController(context);

        Assert.IsType<BadRequestObjectResult>((await controller.UpdatePlan(
            new InvestmentPlanMutationDto(50, 50, 1, 3, 5))).Result);
        Assert.IsType<BadRequestObjectResult>(await controller.UpdateAllocationSleeve(
            first.Id, new AllocationSleeveMutationDto("Unknown")));
        Assert.IsType<BadRequestObjectResult>(await controller.UpdateAllocationOrder(
            new AllocationOrderMutationDto([first.Id, first.Id])));

        var plan = Assert.IsType<InvestmentPlanDto>(
            Assert.IsType<OkObjectResult>((await controller.UpdatePlan(
                new InvestmentPlanMutationDto(60, 20, 20, 3, 5))).Result).Value);
        Assert.Equal(60, plan.UsEquityTarget);
        Assert.IsType<NoContentResult>(await controller.UpdateAllocationSleeve(
            first.Id, new AllocationSleeveMutationDto("USEquity")));
        Assert.IsType<NoContentResult>(await controller.UpdateAllocationOrder(
            new AllocationOrderMutationDto([second.Id, first.Id])));

        var allocation = Assert.IsType<InvestmentAllocationOverviewDto>(
            Assert.IsType<OkObjectResult>((await controller.GetAllocation()).Result).Value);
        Assert.Equal("USD", allocation.AppCurrency);
    }

    [Fact]
    public async Task ClassificationRoutesAreTenantScoped()
    {
        await using var context = TestHelpers.NewInMemoryContext("alice");
        var bob = new InvestmentInstrument
        {
            UserId = "bob", Symbol = "BOB", Name = "Bob fund", Type = "ETF", Currency = "USD"
        };
        context.SetCurrentUser("bob");
        context.InvestmentInstruments.Add(bob);
        await context.SaveChangesAsync();
        context.SetCurrentUser("alice");

        var controller = NewController(context);
        Assert.IsType<NotFoundResult>(await controller.UpdateAllocationSleeve(
            bob.Id, new AllocationSleeveMutationDto("USEquity")));
        Assert.IsType<BadRequestObjectResult>(await controller.UpdateAllocationOrder(
            new AllocationOrderMutationDto([bob.Id])));
    }

    private static InvestmentAllocationController NewController(Database.AppDbContext context)
        => new(
            new InvestmentPortfolioService(context, new InvestmentAccountingService(), new TestProvider()),
            new InvestmentAllocationService(context))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private sealed class TestProvider : IMarketDataProvider
    {
        public MarketDataProviderDescriptor Descriptor => new(
            "allocation-test", "Allocation test provider", false,
            MarketDataCapabilities.RequiredForActivation,
            new MarketDataQuotaPolicy(10, 100, 100));

        public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InstrumentSearchResult>>([]);
        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(
            MarketInstrumentReference instrument, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderPriceBar>>([]);
        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(
            string baseCurrency, string quoteCurrency, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderFxBar>>([]);
        public MarketInstrumentReference? TryResolveLegacyReference(string? symbol, string? mic) => null;
    }
}
