using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

public class AiIntentResolverTests
{
    [Fact]
    public void ResolveDeterministically_DeleteCommand_SetsLedgerEditIntent()
    {
        var plan = AiAssistantService.ResolveDeterministically("delete the coffee transaction from yesterday");
        Assert.Contains(AiAssistantService.AiIntent.LedgerEdit, plan.Intents);
    }

    [Fact]
    public void ResolveDeterministically_ExplicitAmountThreshold_SetsTransactionListIntent()
    {
        var plan = AiAssistantService.ResolveDeterministically("what purchases were over 50 dollars?");
        Assert.Contains(AiAssistantService.AiIntent.LedgerTransactionList, plan.Intents);
        Assert.True(plan.QueryPlan.NeedsTransactionDetail);
    }

    [Fact]
    public void ResolveDeterministically_CountQuestion_SetsLedgerActivityCountIntent()
    {
        var plan = AiAssistantService.ResolveDeterministically("how many transactions did I have in March?");
        Assert.Contains(AiAssistantService.AiIntent.LedgerActivityCount, plan.Intents);
        Assert.Contains(AiAssistantService.DerivedMetric.ActivityCount, plan.QueryPlan.Metrics);
        Assert.True(plan.QueryPlan.NeedsTransactionDetail); // Needs matches for count
    }

    [Fact]
    public void ResolveDeterministically_WishlistCreation_SetsWishlistAddIntent()
    {
        var plan = AiAssistantService.ResolveDeterministically("add a new car to my wishlist");
        Assert.Contains(AiAssistantService.AiIntent.WishlistAdd, plan.Intents);
        Assert.True(plan.QueryPlan.NeedsWishlist);
    }

    [Fact]
    public void ResolveDeterministically_RecurringCreation_SetsRecurringAddIntent()
    {
        var plan = AiAssistantService.ResolveDeterministically("add a new netflix subscription");
        Assert.Contains(AiAssistantService.AiIntent.RecurringAdd, plan.Intents);
        Assert.True(plan.QueryPlan.NeedsRecurring);
    }

    [Fact]
    public void ResolveDeterministically_NegatedTopics_AreExcludedFromQueryPlan()
    {
        // "excluding recurring" forces NeedsRecurring = false even if the system normally pulls it
        var plan = AiAssistantService.ResolveDeterministically("how much did I spend this month excluding recurring subscriptions?");
        Assert.False(plan.QueryPlan.NeedsRecurring);
    }

    [Fact]
    public void ResolveDeterministically_WishlistForecast_SetsForecastIntent()
    {
        var plan = AiAssistantService.ResolveDeterministically("when can I afford the items on my wishlist?");
        Assert.Contains(AiAssistantService.AiIntent.WishlistForecast, plan.Intents);
        Assert.True(plan.QueryPlan.NeedsWishlistForecast);
        Assert.True(plan.QueryPlan.NeedsWishlist);
    }

    [Fact]
    public void ResolveDeterministically_AnomalyDetection_SetsLedgerAnomalyIntent()
    {
        var plan = AiAssistantService.ResolveDeterministically("were there any unusual expenses last month?");
        Assert.Contains(AiAssistantService.AiIntent.LedgerAnomaly, plan.Intents);
    }
}
