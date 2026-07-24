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

    [Theory]
    [InlineData("Which category limits am I likely to exceed this cycle?")]
    [InlineData("What limit do I currently have?")]
    [InlineData("Show my limits")]
    [InlineData("Limits")]
    [InlineData("Am I within my spending cap?")]
    public void ResolveDeterministically_CategoryLimitQuestion_LoadsLimitAndCycleContext(string query)
    {
        var plan = AiAssistantService.ResolveDeterministically(query);

        Assert.Contains(AiAssistantService.AiIntent.CategoryLimits, plan.Intents);
        Assert.True(plan.QueryPlan.NeedsCategoryLimits);
        Assert.True(plan.QueryPlan.NeedsCycleSummary);
    }

    [Theory]
    [InlineData("Give me a cycle summary for this cycle")]
    [InlineData("How am I doing this cycle?")]
    [InlineData("How is my current cycle going?")]
    [InlineData("This month so far")]
    [InlineData("Current cycle")]
    [InlineData("Recap")]
    public void ResolveDeterministically_CycleSummaryQuestion_LoadsSupplementalInsights(string query)
    {
        var plan = AiAssistantService.ResolveDeterministically(query);

        Assert.Contains(AiAssistantService.AiIntent.CycleInsights, plan.Intents);
        Assert.True(plan.QueryPlan.NeedsCycleInsights);
        Assert.True(plan.QueryPlan.NeedsCycleSummary);
    }

    [Fact]
    public void ResolveDeterministically_PushReminderQuestion_LoadsRecurringContext()
    {
        var plan = AiAssistantService.ResolveDeterministically("Are my subscription push reminders enabled?");

        Assert.Contains(AiAssistantService.AiIntent.RecurringList, plan.Intents);
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

    [Fact]
    public void ResolveDeterministically_CycleAnalysisKeywords_SetsSpendingTotalIntent()
    {
        var queries = new[]
        {
            "what were my expenses this month",
            "show me my costs for january",
            "what are my fees",
            "what is my profit margin"
        };

        foreach (var query in queries)
        {
            var plan = AiAssistantService.ResolveDeterministically(query);
            Assert.Contains(AiAssistantService.AiIntent.LedgerSpendingTotal, plan.Intents);
            Assert.True(plan.QueryPlan.NeedsCycleSummary, $"Failed for query: {query}");
        }
    }

    [Fact]
    public void ResolveDeterministically_TransactionDetailKeywords_SetsTransactionListIntent()
    {
        var queries = new[]
        {
            "find my invoices",
            "search for bills",
            "which transactions cost me the most",
            "show priced items"
        };

        foreach (var query in queries)
        {
            var plan = AiAssistantService.ResolveDeterministically(query);
            Assert.Contains(AiAssistantService.AiIntent.LedgerTransactionList, plan.Intents);
            Assert.True(plan.QueryPlan.NeedsTransactionDetail, $"Failed for query: {query}");
        }
    }

    [Fact]
    public void ResolveDeterministically_RecurringKeywords_SetsRecurringIntents()
    {
        var queries = new[]
        {
            "show my subs",
            "what are my direct debits",
            "annual payment schedule",
            "auto-renewal list"
        };

        foreach (var query in queries)
        {
            var plan = AiAssistantService.ResolveDeterministically(query);
            Assert.True(plan.Intents.Contains(AiAssistantService.AiIntent.RecurringList) || plan.Intents.Contains(AiAssistantService.AiIntent.RecurringUpcoming));
            Assert.True(plan.QueryPlan.NeedsRecurring, $"Failed for query: {query}");
        }
    }

    [Fact]
    public void ResolveDeterministically_WishlistKeywords_SetsWishlistIntents()
    {
        var queries = new[]
        {
            "show my wish-list",
            "what am I saving up for",
            "save up items"
        };

        foreach (var query in queries)
        {
            var plan = AiAssistantService.ResolveDeterministically(query);
            Assert.Contains(AiAssistantService.AiIntent.WishlistList, plan.Intents);
            Assert.True(plan.QueryPlan.NeedsWishlist, $"Failed for query: {query}");
        }
    }

    [Fact]
    public void ResolveDeterministically_WishlistForecastKeywords_SetsForecastIntent()
    {
        var queries = new[]
        {
            "how many months to save for a car",
            "time to save for a house",
            "when could i afford a boat"
        };

        foreach (var query in queries)
        {
            var plan = AiAssistantService.ResolveDeterministically(query);
            Assert.Contains(AiAssistantService.AiIntent.WishlistForecast, plan.Intents);
            Assert.True(plan.QueryPlan.NeedsWishlistForecast, $"Failed for query: {query}");
            Assert.True(plan.QueryPlan.NeedsWishlist, $"Failed for query: {query}");
        }
    }

    [Fact]
    public void ResolveDeterministically_ImprovementKeywords_SetsAllocationIntent()
    {
        var queries = new[]
        {
            "how is my budgeting",
            "show me my plan",
            "what are my goals"
        };

        foreach (var query in queries)
        {
            var plan = AiAssistantService.ResolveDeterministically(query);
            Assert.True(plan.QueryPlan.NeedsBudgetTargets, $"Failed for query: {query}");
        }
    }
}
