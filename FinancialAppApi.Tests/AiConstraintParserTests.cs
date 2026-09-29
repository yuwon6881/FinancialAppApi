using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

// The two request constraints action validation enforces from the user's own words.
public class AiConstraintParserTests
{
    [Theory]
    [InlineData("Don't open the ledger, just tell me the total.")]
    [InlineData("please don't take me anywhere, what did I spend?")]
    public void NegatedNavigation_SetsPreventNavigation(string message)
    {
        Assert.True(AiAssistantService.ParseConstraints(message).PreventNavigation);
    }

    [Theory]
    [InlineData("What if I save another RM200 per cycle?")]
    [InlineData("Suppose my salary increases next year")]
    public void HypotheticalFraming_IsHypothetical(string message)
    {
        Assert.True(AiAssistantService.ParseConstraints(message).Hypothetical);
    }

    [Theory]
    [InlineData("How much did I spend this month?")]
    [InlineData("open the ledger")]
    [InlineData("")]
    public void PlainRequests_HaveNoConstraints(string message)
    {
        var constraints = AiAssistantService.ParseConstraints(message);

        Assert.False(constraints.PreventNavigation);
        Assert.False(constraints.Hypothetical);
    }
}
