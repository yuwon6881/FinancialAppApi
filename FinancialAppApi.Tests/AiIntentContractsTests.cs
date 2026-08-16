using FinancialAppApi.Services;
using Intent = FinancialAppApi.Services.AiAssistantService.AiIntent;

namespace FinancialAppApi.Tests;

// Phase 1: typed intent vocabulary maps losslessly to/from the dotted string names.
public class AiIntentContractsTests
{
    [Theory]
    [InlineData("ledger.activity_count", Intent.LedgerActivityCount)]
    [InlineData("ledger.account", Intent.LedgerAccount)]
    [InlineData("wishlist.forecast", Intent.WishlistForecast)]
    [InlineData("allocation.performance", Intent.AllocationPerformance)]
    [InlineData("navigation", Intent.Navigation)]
    [InlineData("general", Intent.General)]
    public void ParseIntent_MapsKnownNames(string name, Intent expected)
    {
        Assert.Equal(expected, AiAssistantService.ParseIntent(name));
    }

    [Fact]
    public void ParseIntent_UnknownName_IsUnknown()
    {
        Assert.Equal(Intent.Unknown, AiAssistantService.ParseIntent("totally.fake"));
    }

    [Fact]
    public void RoundTrip_NameToIntentToName_IsStable()
    {
        foreach (var name in new[] { "ledger.spending_total", "recurring.upcoming", "wishlist.edit" })
        {
            Assert.Equal(name, AiAssistantService.ToIntentName(AiAssistantService.ParseIntent(name)));
        }
    }

    [Fact]
    public void CapabilityRegistry_CoversEverySupportedIntentExactlyOnce()
    {
        var supported = Enum.GetValues<Intent>().Where(intent => intent != Intent.Unknown).ToList();

        Assert.Equal(supported.Count, AiAssistantService.AiCapabilities.Count);
        Assert.Equal(supported.Order(), AiAssistantService.AiCapabilities.Select(capability => capability.Intent).Order());
        Assert.Equal(
            AiAssistantService.AiCapabilities.Count,
            AiAssistantService.AiCapabilities.Select(capability => capability.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ParseIntents_DropsUnknownAndDedupes()
    {
        var result = AiAssistantService.ParseIntents(["ledger.edit", "nope", "ledger.edit", "navigation"]);
        Assert.Equal([Intent.LedgerEdit, Intent.Navigation], result);
    }

    [Fact]
    public void SavingsGoalPacing_IsNotResolvedAsWishlist()
    {
        var plan = AiAssistantService.ResolveDeterministically("How are my savings goals pacing?");

        Assert.Contains(Intent.SavingsGoalPacing, plan.Intents);
        Assert.DoesNotContain(Intent.WishlistList, plan.Intents);
        Assert.DoesNotContain(Intent.WishlistForecast, plan.Intents);
    }

    [Fact]
    public void InvestmentGrowth_IsNotResolvedAsGrowthBudgetAllocation()
    {
        var plan = AiAssistantService.ResolveDeterministically("Explain my Growth portfolio and holdings");

        Assert.Contains(Intent.InvestmentHolding, plan.Intents);
        Assert.True(plan.QueryPlan.NeedsInvestments);
        Assert.DoesNotContain(Intent.AllocationBalance, plan.Intents);
        Assert.DoesNotContain(Intent.AllocationPerformance, plan.Intents);

        var summary = AiAssistantService.ResolveDeterministically("Explain my portfolio");
        Assert.Contains(Intent.InvestmentSummary, summary.Intents);
    }

    [Fact]
    public void ReportReview_RequestsCycleSummaryAndReportDataset()
    {
        var plan = AiAssistantService.ResolveDeterministically("Review this cycle and explain the findings");

        Assert.Contains(Intent.ReportReview, plan.Intents);
        Assert.True(plan.QueryPlan.NeedsCycleSummary);
        Assert.True(plan.QueryPlan.NeedsReport);
    }
}
