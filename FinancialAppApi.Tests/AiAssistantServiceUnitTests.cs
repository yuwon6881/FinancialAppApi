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
    public void SanitizeConversationState_PreservesEverySupportedDomainFrame()
    {
        var instrumentId = Guid.NewGuid();
        var state = EmptyState() with
        {
            LastIntent = "investment.allocation",
            LastIntents = ["investment.allocation", "report.review", "savings_goal.pacing"],
            LastTopic = "investment",
            LastRewardsTopic = "plan",
            LastSavingsGoalId = 42,
            LastInvestmentTopic = "portfolio",
            LastInvestmentRange = "3m",
            LastInvestmentInstrumentId = instrumentId,
            LastReportCycleKey = "2026-08",
            LastLedgerAccountId = "acct-maybank"
        };

        var sanitized = Services.AiAssistantService.SanitizeConversationState(state);

        Assert.NotNull(sanitized);
        Assert.Equal("investment.allocation", sanitized!.LastIntent);
        Assert.Equal(["investment.allocation", "report.review", "savings_goal.pacing"], sanitized.LastIntents);
        Assert.Equal("investment", sanitized.LastTopic);
        Assert.Equal("plan", sanitized.LastRewardsTopic);
        Assert.Equal(42, sanitized.LastSavingsGoalId);
        Assert.Equal("portfolio", sanitized.LastInvestmentTopic);
        Assert.Equal("3m", sanitized.LastInvestmentRange);
        Assert.Equal(instrumentId, sanitized.LastInvestmentInstrumentId);
        Assert.Equal("2026-08", sanitized.LastReportCycleKey);
        Assert.Equal("acct-maybank", sanitized.LastLedgerAccountId);
    }

    [Fact]
    public void SanitizeConversationState_RejectsInvalidExtendedDomainFields()
    {
        var state = EmptyState() with
        {
            LastTopic = "unknown",
            LastRewardsTopic = new string('x', 100),
            LastSavingsGoalId = -1,
            LastInvestmentRange = "forever",
            LastReportCycleKey = "not-a-cycle"
        };

        var sanitized = Services.AiAssistantService.SanitizeConversationState(state);

        Assert.NotNull(sanitized);
        Assert.Null(sanitized!.LastTopic);
        Assert.Null(sanitized.LastRewardsTopic);
        Assert.Null(sanitized.LastSavingsGoalId);
        Assert.Null(sanitized.LastInvestmentRange);
        Assert.Null(sanitized.LastReportCycleKey);
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

    [Theory]
    [InlineData("I prepared both transactions for review.")]
    [InlineData("The records are ready for review.")]
    [InlineData("I added those transactions.")]
    public void EnforceActionBackedDraftClaims_RejectsEquivalentClaimsWithoutActions(string reply)
    {
        var result = Services.AiAssistantService.EnforceActionBackedDraftClaims(
            new Services.AiChatResponse(reply, []),
            sensitiveMode: false);

        Assert.StartsWith("Nothing was added", result.Reply);
    }

    [Fact]
    public void EnforceActionBackedDraftClaims_ReportsTheCommittedActionCount()
    {
        var actions = new[]
        {
            new Services.AiUiAction("openAddLedgerDraft", []),
            new Services.AiUiAction("openAddLedgerDraft", [])
        };

        var result = Services.AiAssistantService.EnforceActionBackedDraftClaims(
            new Services.AiChatResponse("I prepared three transactions.", actions),
            sensitiveMode: false);

        Assert.Equal("I prepared 2 drafts for review. Check each one before saving.", result.Reply);
    }

    [Theory]
    [InlineData("What are my goals?")]
    [InlineData("Can I afford badminton this month?")]
    [InlineData("Show my bills")]
    [InlineData("How much room do I have for Food?")]
    [InlineData("How is my nest egg performing?")]
    public void SemanticPlanner_ReviewsKeywordCollisions(string message)
    {
        var deterministic = Services.AiAssistantService.ResolveDeterministically(message);

        Assert.True(Services.AiAssistantService.ShouldUseSemanticPlanner(message, deterministic, null));
    }

    [Fact]
    public void SemanticPlanner_SkipsExactLedgerShorthand()
    {
        const string message = "Badminton 10";
        var deterministic = Services.AiAssistantService.ResolveDeterministically(message);

        Assert.False(Services.AiAssistantService.ShouldUseSemanticPlanner(message, deterministic, null));
    }

    [Theory]
    [InlineData("Explain my loan payoff")]
    [InlineData("How much interest remains on my mortgage?")]
    public void LoanQuestions_RequestOnlyLoanGrounding(string message)
    {
        var plan = Services.AiAssistantService.ResolveDeterministically(message);

        Assert.Contains(Services.AiAssistantService.AiIntent.LoanSummary, plan.Intents);
        Assert.True(plan.QueryPlan.NeedsLoans);
        Assert.False(plan.QueryPlan.NeedsTransactionDetail);
        Assert.False(plan.QueryPlan.NeedsRecurring);
    }

    [Fact]
    public void LoanFollowUp_PreservesSelectedLoanReference()
    {
        var prior = EmptyState() with
        {
            LastIntent = "loan.summary",
            LastIntents = ["loan.summary"],
            LastTopic = "loan",
            LastLoanId = "loan-home"
        };

        var plan = Services.AiAssistantService.ResolveDeterministically("What about the next payment?", prior);

        Assert.Contains(Services.AiAssistantService.AiIntent.LoanSummary, plan.Intents);
        Assert.Equal("loan-home", plan.ConversationState.LastLoanId);
    }
}
