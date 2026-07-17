namespace FinancialAppApi.Tests;

public class AiAssistantServiceUnitTests
{
    // ---------- IsContextResetRequest Tests ----------

    [Theory]
    [InlineData("forget that")]
    [InlineData("nevermind")]
    [InlineData("start over")]
    [InlineData("reset")]
    [InlineData("new topic")]
    [InlineData("clear context")]
    [InlineData("ignore previous")]
    [InlineData("scratch that")]
    [InlineData("different topic")]
    [InlineData("actually, never mind")]
    public void IsContextResetRequest_MatchesKnownPhrases(string phrase)
    {
        Assert.True(Services.AiAssistantService.IsContextResetRequest(phrase));
    }

    [Theory]
    [InlineData("how much did I spend")]
    [InlineData("forget to buy milk")]
    [InlineData("what is my new topic")]
    [InlineData("reset my password")]
    public void IsContextResetRequest_IgnoresUnrelatedPhrases(string phrase)
    {
        Assert.False(Services.AiAssistantService.IsContextResetRequest(phrase));
    }

    // ---------- SanitizeConversationState Tests ----------
    private static Services.AiConversationState EmptyState() => new(null, null, null, null);

    [Fact]
    public void SanitizeConversationState_PreservesValidState()
    {
        var state = EmptyState() with
        {
            LastLedgerCategory = "Essentials",
            LastTransactionType = "outflow",
            LastTargetAmount = 50.0m,
            LastAmountThreshold = new Services.AiAmountThreshold("GreaterThan", 50m),
            LastSearchText = "lunch",
            LastExcludeTransfers = true,
            LastExcludedCategories = new[] { "Rent" },
            LastIncludedCategories = new[] { "Dining" }
        };

        var sanitized = Services.AiAssistantService.SanitizeConversationState(state);
        Assert.NotNull(sanitized);

        Assert.Equal("Essentials", sanitized!.LastLedgerCategory);
        Assert.Equal("outflow", sanitized.LastTransactionType);
        Assert.Equal(50.0m, sanitized.LastTargetAmount);
        Assert.Equal("GreaterThan", sanitized.LastAmountThreshold?.Comparator);
        Assert.Equal(50m, sanitized.LastAmountThreshold?.Low);
        Assert.Equal("lunch", sanitized.LastSearchText);
        Assert.True(sanitized.LastExcludeTransfers);
        Assert.Equal(new[] { "Rent" }, sanitized.LastExcludedCategories);
        Assert.Equal(new[] { "Dining" }, sanitized.LastIncludedCategories);
    }

    [Fact]
    public void SanitizeConversationState_RejectsInvalidTransactionType()
    {
        var state = EmptyState() with { LastTransactionType = "invalid" };
        var sanitized = Services.AiAssistantService.SanitizeConversationState(state);
        Assert.NotNull(sanitized);
        Assert.Null(sanitized!.LastTransactionType);
    }

    [Fact]
    public void SanitizeConversationState_RejectsInvalidComparator()
    {
        var state = EmptyState() with { LastAmountThreshold = new Services.AiAmountThreshold("invalid", 50m) };
        var sanitized = Services.AiAssistantService.SanitizeConversationState(state);
        Assert.NotNull(sanitized);
        Assert.Null(sanitized!.LastAmountThreshold);
    }

    [Fact]
    public void SanitizeConversationState_CapsTargetAmount()
    {
        var state = EmptyState() with { LastTargetAmount = 2000000000m };
        var sanitized = Services.AiAssistantService.SanitizeConversationState(state);
        Assert.NotNull(sanitized);
        Assert.Null(sanitized!.LastTargetAmount);
    }

    [Fact]
    public void SanitizeConversationState_RejectsNegativeTargetAmount()
    {
        var state = EmptyState() with { LastTargetAmount = -50m };
        var sanitized = Services.AiAssistantService.SanitizeConversationState(state);
        Assert.NotNull(sanitized);
        Assert.Null(sanitized!.LastTargetAmount);
    }
}
