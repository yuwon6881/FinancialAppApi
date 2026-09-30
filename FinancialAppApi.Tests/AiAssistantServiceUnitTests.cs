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

    // ---------- SanitizeConversationState ----------

    [Fact]
    public void SanitizeConversationState_KeepsValidReferences()
    {
        var state = new Services.AiConversationState(
            ["t1", "t2", "t1"], 7, "1Y", "2026-08", "loan-home", "transfer 50 from cimb to ryt");

        var sanitized = Services.AiAssistantService.SanitizeConversationState(state)!;

        Assert.Equal(["t1", "t2"], sanitized.LastMatchedTransactionIds!);
        Assert.Equal(7, sanitized.LastSavingsGoalId);
        Assert.Equal("1y", sanitized.LastInvestmentRange);
        Assert.Equal("2026-08", sanitized.LastReportCycleKey);
        Assert.Equal("loan-home", sanitized.LastLoanId);
        Assert.Equal("transfer 50 from cimb to ryt", sanitized.PendingLedgerRequest);
    }

    [Fact]
    public void SanitizeConversationState_DropsTamperedValues()
    {
        var state = new Services.AiConversationState(
            [new string('x', 65), " "], -3, "10y", "2026-13", new string('l', 101), new string('p', 2001));

        var sanitized = Services.AiAssistantService.SanitizeConversationState(state)!;

        Assert.Null(sanitized.LastMatchedTransactionIds);
        Assert.Null(sanitized.LastSavingsGoalId);
        Assert.Null(sanitized.LastInvestmentRange);
        Assert.Null(sanitized.LastReportCycleKey);
        Assert.Null(sanitized.LastLoanId);
        Assert.Null(sanitized.PendingLedgerRequest);
    }

    [Fact]
    public void SanitizeConversationState_CapsTheCarriedIdList()
    {
        var ids = Enumerable.Range(0, 80).Select(index => $"t{index}").ToList();

        var sanitized = Services.AiAssistantService.SanitizeConversationState(new Services.AiConversationState(ids))!;

        Assert.Equal(50, sanitized.LastMatchedTransactionIds!.Count);
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
    public void EnforceActionBackedDraftClaims_LeavesANavigationReplyAloneWhenNoChangeWasAsked()
    {
        var result = Services.AiAssistantService.EnforceActionBackedDraftClaims(
            new Services.AiChatResponse("I opened the ledger filtered to haircut.", [new Services.AiUiAction("openLedger", [])]),
            sensitiveMode: false,
            Services.AiAssistantService.DraftRequest.None);

        Assert.Equal("I opened the ledger filtered to haircut.", result.Reply);
    }

    [Fact]
    public void EnforceActionBackedDraftClaims_AFailedEditIsNotAnsweredWithTheLedgerAddHint()
    {
        var result = Services.AiAssistantService.EnforceActionBackedDraftClaims(
            new Services.AiChatResponse("I prepared the edit.", []),
            sensitiveMode: false,
            Services.AiAssistantService.DraftRequest.OtherChange);

        Assert.StartsWith("Nothing was changed", result.Reply);
        Assert.DoesNotContain("Mamak", result.Reply);
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
}
