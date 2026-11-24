using FinancialAppApi.Controllers;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FinancialAppApi.Tests;

public sealed class InvestmentAllocationControllerTests
{
    [Fact]
    public async Task GroupsAndTargetsCanBeToggledAndTheLastGroupCanBeRemoved()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var fund = new InvestmentInstrument { Symbol = "FUND", Name = "Fund", Type = "ETF" };
        context.InvestmentInstruments.Add(fund);
        await context.SaveChangesAsync();
        var controller = NewController(context);
        async Task<InvestmentAllocationOverviewDto> Read() =>
            Assert.IsType<InvestmentAllocationOverviewDto>(
                Assert.IsType<OkObjectResult>((await controller.GetAllocation()).Result).Value);
        var initial = await Read();
        Assert.False(initial.Plan.GroupingEnabled);
        Assert.False(initial.Plan.TargetsEnabled);
        Assert.Empty(initial.Plan.Groups);

        var group = new InvestmentGroup("a1111111111111111111111111111111", "Income", 100, null);
        var plan = new InvestmentPlanMutationDto(66, 10, 24, 3, 5, [group], true, false);
        Assert.IsType<OkObjectResult>((await controller.UpdatePlan(plan)).Result);
        Assert.IsType<NoContentResult>(await controller.UpdateAllocationSleeve(fund.Id, new(group.Key)));
        var grouped = await Read();
        Assert.Null(Assert.Single(grouped.Sleeves).TargetPercentage);
        Assert.Null(grouped.ContributionPlan);

        Assert.IsType<OkObjectResult>((await controller.UpdatePlan(plan with { GroupingEnabled = false, TargetsEnabled = true })).Result);
        var disabled = await Read();
        Assert.Empty(disabled.Sleeves);
        Assert.Single(disabled.Plan.Groups);
        Assert.Equal(group.Key, Assert.Single(disabled.Assignments).Sleeve);
        Assert.IsType<BadRequestObjectResult>(await controller.UpdateAllocationSleeve(fund.Id, new(group.Key)));

        Assert.IsType<OkObjectResult>((await controller.UpdatePlan(plan with { TargetsEnabled = true })).Result);
        Assert.Equal(100, Assert.Single((await Read()).Sleeves).TargetPercentage);
        Assert.IsType<BadRequestObjectResult>((await controller.UpdatePlan(plan with { Groups = [] })).Result);
        Assert.IsType<NoContentResult>(await controller.UpdateAllocationSleeve(fund.Id, new(null)));
        Assert.IsType<OkObjectResult>((await controller.UpdatePlan(plan with { Groups = [] })).Result);
        Assert.Empty((await Read()).Plan.Groups);
    }

    [Fact]
    public async Task PlanAndClassificationRoutesValidateAndPersist()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var first = new InvestmentInstrument { Symbol = "AAA", Name = "First", Type = "ETF", Currency = "USD" };
        var second = new InvestmentInstrument { Symbol = "BBB", Name = "Second", Type = "ETF", Currency = "USD" };
        context.InvestmentInstruments.AddRange(first, second);
        context.InvestmentPlans.Add(InvestmentAllocationServiceTests.MigratedPlan());
        await context.SaveChangesAsync();
        var controller = NewController(context);

        Assert.IsType<BadRequestObjectResult>((await controller.UpdatePlan(
            new InvestmentPlanMutationDto(50, 50, 1, 3, 5))).Result);
        Assert.IsType<BadRequestObjectResult>(await controller.UpdateAllocationSleeve(
            first.Id, new AllocationSleeveMutationDto("Unknown")));
        Assert.IsType<BadRequestObjectResult>(await controller.UpdateAllocationOrder(
            new AllocationOrderMutationDto([first.Id, first.Id])));
        Assert.IsType<BadRequestObjectResult>(await controller.UpdateAllocationOrder(
            new AllocationOrderMutationDto([first.Id])));

        var plan = Assert.IsType<InvestmentPlanDto>(
            Assert.IsType<OkObjectResult>((await controller.UpdatePlan(
                new InvestmentPlanMutationDto(60, 20, 20, 3, 5))).Result).Value);
        Assert.Equal(60, plan.Groups.Single(group => group.Key == "USEquity").TargetPercentage);
        Assert.IsType<NoContentResult>(await controller.UpdateAllocationSleeve(
            first.Id, new AllocationSleeveMutationDto("USEquity")));
        Assert.IsType<NoContentResult>(await controller.UpdateAllocationOrder(
            new AllocationOrderMutationDto([second.Id, first.Id])));

        var allocation = Assert.IsType<InvestmentAllocationOverviewDto>(
            Assert.IsType<OkObjectResult>((await controller.GetAllocation()).Result).Value);
        Assert.Equal("USD", allocation.AppCurrency);
    }

    [Fact]
    public async Task CustomGroupsAreUsedForAllocationAndCannotBeRemovedWhileAssigned()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var fund = new InvestmentInstrument { Symbol = "VNQ", Name = "Real estate fund", Type = "ETF", Currency = "USD" };
        context.InvestmentInstruments.Add(fund);
        context.InvestmentPlans.Add(InvestmentAllocationServiceTests.MigratedPlan());
        await context.SaveChangesAsync();
        var controller = NewController(context);
        const string groupKey = "a1111111111111111111111111111111";
        var custom = new InvestmentGroup(groupKey, "Real estate", 30, null);
        var shares = new InvestmentGroup("USEquity", "US Equity", 70, "USEquity");
        var plan = new InvestmentPlanMutationDto(66, 10, 24, 5, 10, [shares, custom]);
        var reducedPlan = plan with { Groups = [shares with { TargetPercentage = 100 }] };

        Assert.IsType<OkObjectResult>((await controller.UpdatePlan(plan)).Result);
        Assert.IsType<NoContentResult>(await controller.UpdateAllocationSleeve(fund.Id, new(groupKey)));
        var allocation = Assert.IsType<InvestmentAllocationOverviewDto>(
            Assert.IsType<OkObjectResult>((await controller.GetAllocation()).Result).Value);
        Assert.Equal(new[] { "US Equity", "Real estate" }, allocation.Sleeves.Select(group => group.Label));
        Assert.Equal(groupKey, Assert.Single(allocation.Assignments).Sleeve);
        Assert.IsType<BadRequestObjectResult>((await controller.UpdatePlan(reducedPlan)).Result);

        Assert.IsType<NoContentResult>(await controller.UpdateAllocationSleeve(fund.Id, new("USEquity")));
        Assert.IsType<OkObjectResult>((await controller.UpdatePlan(reducedPlan)).Result);
        Assert.IsType<BadRequestObjectResult>(await controller.UpdateAllocationSleeve(fund.Id, new(groupKey)));
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
