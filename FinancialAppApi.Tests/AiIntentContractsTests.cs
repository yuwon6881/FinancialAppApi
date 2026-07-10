using FinancialAppApi.Services;
using Intent = FinancialAppApi.Services.AiAssistantService.AiIntent;

namespace FinancialAppApi.Tests;

// Phase 1: typed intent vocabulary maps losslessly to/from the dotted string names.
public class AiIntentContractsTests
{
    [Theory]
    [InlineData("ledger.activity_count", Intent.LedgerActivityCount)]
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
    public void ParseIntents_DropsUnknownAndDedupes()
    {
        var result = AiAssistantService.ParseIntents(["ledger.edit", "nope", "ledger.edit", "navigation"]);
        Assert.Equal([Intent.LedgerEdit, Intent.Navigation], result);
    }
}
