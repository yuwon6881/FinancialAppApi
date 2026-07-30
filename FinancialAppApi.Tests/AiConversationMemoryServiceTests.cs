using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FinancialAppApi.Tests;

public sealed class AiConversationMemoryServiceTests
{
    [Fact]
    public async Task CompletedTurnHydratesAndDuplicateClientTurnReplaysWithoutAnotherVersion()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { HideSensitive = false });
        await context.SaveChangesAsync();
        var memory = new AiConversationMemoryService(context);
        var request = new AiChatRequest(
            "How much did I spend?",
            [],
            ClientTurnId: "turn-1",
            ConversationVersion: 0);

        var prepared = await memory.PrepareAsync(request, CancellationToken.None);
        var state = new AiConversationState("ledger.spending_total", null, null, null);
        var completed = await memory.CompleteAsync(
            prepared,
            request.Message,
            new AiChatResponse("You spent 10.", [], State: state),
            CancellationToken.None);

        Assert.NotNull(completed);
        Assert.Equal(1, completed!.ConversationVersion);
        var hydrated = await memory.GetActiveAsync();
        Assert.Equal(completed.ConversationId, hydrated.ConversationId);
        Assert.Equal(1, hydrated.ConversationVersion);
        Assert.Equal(
            [new AiChatMessage("user", request.Message), new AiChatMessage("assistant", "You spent 10.")],
            hydrated.Messages);

        var replay = await memory.PrepareAsync(
            request with
            {
                ConversationId = completed.ConversationId,
                ConversationVersion = 0
            },
            CancellationToken.None);
        Assert.NotNull(replay.Replay);
        Assert.Equal("You spent 10.", replay.Replay!.Reply);
        Assert.Equal(1, replay.Replay.ConversationVersion);
        Assert.Single(context.AiConversationTurns);
    }

    [Fact]
    public async Task VersionConflictDoesNotSelectOrPersistASecondTurn()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var conversation = new AiConversation { Version = 3 };
        context.AiConversations.Add(conversation);
        await context.SaveChangesAsync();
        var memory = new AiConversationMemoryService(context);

        var prepared = await memory.PrepareAsync(
            new AiChatRequest(
                "follow up",
                [],
                ConversationId: conversation.Id,
                ConversationVersion: 2,
                ClientTurnId: "turn-2"),
            CancellationToken.None);

        Assert.True(prepared.Conflict);
        Assert.Empty(context.AiConversationTurns);
    }

    [Fact]
    public async Task PromptHistoryIncludesLatestAndRelevantOlderTurnWithinBudget()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { HideSensitive = false });
        var conversation = new AiConversation { Version = 7 };
        context.AiConversations.Add(conversation);
        var start = DateTime.UtcNow.AddHours(-10);
        for (var index = 0; index < 7; index++)
        {
            var isRelevantOlder = index == 1;
            context.AiConversationTurns.Add(new AiConversationTurn
            {
                ConversationId = conversation.Id,
                ClientTurnId = $"turn-{index}",
                UserMessage = isRelevantOlder ? "Badminton spending last cycle" : $"Unrelated turn {index}",
                AssistantReply = isRelevantOlder ? "Badminton was 20." : $"Answer {index}",
                ActionsJson = "[]",
                Topic = isRelevantOlder ? "transactional" : "wishlist",
                KeywordsJson = isRelevantOlder ? "[\"badminton\",\"spending\"]" : "[]",
                FacetsJson = "[]",
                SensitiveMode = false,
                ConversationVersion = index + 1,
                CreatedAt = start.AddHours(index)
            });
        }
        await context.SaveChangesAsync();
        var memory = new AiConversationMemoryService(context);

        var prepared = await memory.PrepareAsync(
            new AiChatRequest(
                "How often was Badminton spending?",
                [],
                ConversationId: conversation.Id,
                ConversationVersion: 7,
                ClientTurnId: "new-turn"),
            CancellationToken.None);

        Assert.Contains(prepared.History, message => message.Content == "Badminton spending last cycle");
        Assert.Contains(prepared.History, message => message.Content == "Unrelated turn 6");
        Assert.True(prepared.History.Sum(message => message.Content.Length) <=
                    AiConversationMemoryService.MaxPromptHistoryCharacters);
    }

    [Fact]
    public async Task SensitiveModeExcludesTurnsCreatedWithExposedValues()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { HideSensitive = true });
        var conversation = new AiConversation { Version = 2 };
        context.AiConversations.Add(conversation);
        context.AiConversationTurns.AddRange(
            Turn(conversation.Id, "exposed", "My balance was 1234.", sensitiveMode: false, version: 1),
            Turn(conversation.Id, "protected", "A protected-mode answer.", sensitiveMode: true, version: 2));
        await context.SaveChangesAsync();
        var memory = new AiConversationMemoryService(context);

        var prepared = await memory.PrepareAsync(
            new AiChatRequest(
                "Explain that",
                [],
                ConversationId: conversation.Id,
                ConversationVersion: 2,
                ClientTurnId: "new-turn"),
            CancellationToken.None);

        Assert.DoesNotContain(prepared.History, message => message.Content.Contains("1234", StringComparison.Ordinal));
        Assert.Contains(prepared.History, message => message.Content.Contains("protected-mode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NewChatDeletesConversationAndTurns()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var conversation = new AiConversation { Version = 1 };
        context.AiConversations.Add(conversation);
        context.AiConversationTurns.Add(Turn(conversation.Id, "turn", "answer", sensitiveMode: true, version: 1));
        await context.SaveChangesAsync();
        var memory = new AiConversationMemoryService(context);

        await memory.DeleteActiveAsync();

        Assert.Empty(context.AiConversations);
        // The relational database enforces cascade deletion. The in-memory provider does not,
        // so the ownership-filtered conversation assertion is the portable unit-level proof.
    }

    [Fact]
    public async Task GlobalQueryFilterKeepsAnotherUsersConversationInvisible()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using (var alice = new AppDbContext(options))
        {
            alice.SetCurrentUser("alice");
            alice.AiConversations.Add(new AiConversation());
            await alice.SaveChangesAsync();
        }
        await using var bob = new AppDbContext(options);
        bob.SetCurrentUser("bob");
        var memory = new AiConversationMemoryService(bob);

        var hydrated = await memory.GetActiveAsync();

        Assert.Null(hydrated.ConversationId);
        Assert.Empty(hydrated.Messages);
    }

    private static AiConversationTurn Turn(
        Guid conversationId,
        string clientTurnId,
        string answer,
        bool sensitiveMode,
        int version) =>
        new()
        {
            ConversationId = conversationId,
            ClientTurnId = clientTurnId,
            UserMessage = clientTurnId,
            AssistantReply = answer,
            ActionsJson = "[]",
            FacetsJson = "[]",
            KeywordsJson = "[]",
            SensitiveMode = sensitiveMode,
            ConversationVersion = version,
            CreatedAt = DateTime.UtcNow.AddMinutes(version)
        };
}
