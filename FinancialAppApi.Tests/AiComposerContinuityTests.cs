using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

// Two things the assistant used to lose between the composer and the draft: a request typed as one
// sentence with two records in it, and the answer to the one question it asked back.
public class AiComposerContinuityTests
{
    [Theory]
    // The exact shape that produced "send it as a description and an amount on one line".
    [InlineData("internal transfer from cimb to ryt RM50, and spent rm53 on grab mart from cimb essential", 2)]
    [InlineData("Nasi Lemak 12", 1)]
    [InlineData("Mamak 18+2.30", 1)]
    [InlineData("spent 20 at Mamak, paid 15 for parking, transferred 100 to savings", 3)]
    public void CountLedgerDraftListRecords_ReadsEveryRecordInTheMessage(string message, int expected)
    {
        Assert.Equal(expected, AiAssistantService.CountLedgerDraftListRecords(message));
    }

    [Theory]
    // A single record whose description happens to contain "and" must stay one record.
    [InlineData("Nasi Lemak and Teh 12", 1)]
    [InlineData("how much did I spend on food and transport", 0)]
    [InlineData("which transactions were over 100, and which under 20?", 0)]
    [InlineData("show my transfers and my income", 0)]
    public void CountLedgerDraftListRecords_LeavesQuestionsAndSingleRecordsAlone(string message, int expected)
    {
        Assert.Equal(expected, AiAssistantService.CountLedgerDraftListRecords(message));
    }

    [Fact]
    public void StripAccountMentionMarkers_LeavesTheRequestReadableAsShorthand()
    {
        var stripped = AiAssistantService.StripAccountMentionMarkers("@CIMB Grab Mart 53");

        Assert.Equal("CIMB Grab Mart 53", stripped);
        Assert.Equal(1, AiAssistantService.CountLedgerDraftListRecords(stripped));
    }

    [Fact]
    public void CombinePendingLedgerRequest_JoinsTheAnswerToTheRequestItAnswers()
    {
        var combined = AiAssistantService.CombinePendingLedgerRequest(
            "internal transfer from cimb to ryt RM50", "yes, cimb to ryt");

        Assert.Equal("internal transfer from cimb to ryt RM50\nyes, cimb to ryt", combined);
    }

    [Fact]
    public void CombinePendingLedgerRequest_WithoutAPendingRequestReturnsTheMessage()
    {
        Assert.Equal("hello", AiAssistantService.CombinePendingLedgerRequest(null, "hello"));
    }

    [Theory]
    [InlineData("yes, cimb to ryt", true)]
    [InlineData("the second one", true)]
    // A question of its own, an abandonment, and a self-contained record each start fresh.
    [InlineData("how much did I spend?", false)]
    [InlineData("never mind", false)]
    [InlineData("Mamak 20.30", false)]
    public void IsPendingLedgerAnswer_OnlyCarriesTheFrameForAnActualAnswer(string message, bool expected)
    {
        Assert.Equal(expected, AiAssistantService.IsPendingLedgerAnswer("transfer 50 from CIMB", message));
    }

    [Fact]
    public void IsPendingLedgerAnswer_WithNothingPendingIsAlwaysFalse()
    {
        Assert.False(AiAssistantService.IsPendingLedgerAnswer(null, "yes"));
    }

    [Fact]
    public void ResolvePendingLedgerRequest_KeepsTheRequestWhenTheTurnEndedInAQuestion()
    {
        var response = new AiChatResponse("Which account is the money coming from?", []);

        var pending = AiAssistantService.ResolvePendingLedgerRequest("transfer 50 from cimb", response, isLedgerAdd: true);

        Assert.Equal("transfer 50 from cimb", pending);
    }

    [Fact]
    public void ResolvePendingLedgerRequest_ClearsOnceADraftIsStaged()
    {
        var response = new AiChatResponse(
            "Which account? I prepared it anyway.",
            [new AiUiAction("openAddLedgerDraft", new Dictionary<string, object?>())]);

        Assert.Null(AiAssistantService.ResolvePendingLedgerRequest("transfer 50 from cimb", response, isLedgerAdd: true));
    }

    [Fact]
    public void ResolvePendingLedgerRequest_IgnoresAnOrdinaryAnsweredQuestion()
    {
        var response = new AiChatResponse("You spent 120. Want the breakdown?", []);

        Assert.Null(AiAssistantService.ResolvePendingLedgerRequest("how much did I spend", response, isLedgerAdd: false));
    }

    [Fact]
    public void SanitizeConversationState_KeepsThePendingRequestWholeRatherThanAsAReference()
    {
        var request = new string('a', 300);
        var state = new AiConversationState(null, null, null, null) with { PendingLedgerRequest = request };

        var sanitized = AiAssistantService.SanitizeConversationState(state);

        // Reference fields are clamped to 80 characters; a whole request must survive intact or
        // the next turn would carry out half an instruction.
        Assert.Equal(request, sanitized!.PendingLedgerRequest);
    }
}
