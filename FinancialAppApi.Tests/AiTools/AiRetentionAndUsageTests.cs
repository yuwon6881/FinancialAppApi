using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FinancialAppApi.Tests.AiTools;

public class AiRetentionAndUsageTests
{
    [Fact]
    public async Task Retention_AgesOutOldTurnsAbandonedPendingTurnsAndOldUsageRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = NewSqliteContext(connection);
        await context.Database.EnsureCreatedAsync();
        context.AppUsers.Add(new AppUser { Id = TestHelpers.DefaultUserId, Username = "ai", NormalizedUsername = "AI" });
        var conversation = new AiConversation { UserId = TestHelpers.DefaultUserId };
        context.AiConversations.Add(conversation);
        var now = DateTime.UtcNow;
        context.AiConversationTurns.AddRange(
            Turn(conversation, "old", "Completed", now.AddDays(-120)),
            Turn(conversation, "recent", "Completed", now.AddDays(-2)),
            Turn(conversation, "abandoned", "Pending", now.AddMinutes(-30)),
            Turn(conversation, "in-flight", "Pending", now.AddMinutes(-1)));
        var today = DateOnly.FromDateTime(now);
        context.AiUsageDays.AddRange(
            new AiUsageDay { UserId = TestHelpers.DefaultUserId, Date = today.AddDays(-90), InputTokens = 1 },
            new AiUsageDay { UserId = TestHelpers.DefaultUserId, Date = today, InputTokens = 1 });
        await context.SaveChangesAsync();

        var result = await NewRetention(context).PruneAsync();

        Assert.Equal(2, result.AiTurns);
        Assert.Equal(1, result.AiUsageDays);
        Assert.Equal(["in-flight", "recent"], context.AiConversationTurns.IgnoreQueryFilters()
            .Select(turn => turn.ClientTurnId).OrderBy(id => id).ToList());
        Assert.Equal([today], context.AiUsageDays.IgnoreQueryFilters().Select(day => day.Date).ToList());
    }

    [Fact]
    public async Task Memory_ReleasesAPendingTurnACrashLeftBehindSoItsRetryRuns()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { HideSensitive = false });
        var conversation = new AiConversation { UserId = TestHelpers.DefaultUserId };
        context.AiConversations.Add(conversation);
        context.AiConversationTurns.AddRange(
            Turn(conversation, "stuck", "Pending", DateTime.UtcNow.AddMinutes(-30)),
            Turn(conversation, "running", "Pending", DateTime.UtcNow.AddMinutes(-1)));
        await context.SaveChangesAsync();
        var memory = new AiConversationMemoryService(context);

        var retried = await memory.PrepareAsync(new AiChatRequest("hello", [], ClientTurnId: "stuck"), CancellationToken.None);
        var stillRunning = await memory.PrepareAsync(new AiChatRequest("hello", [], ClientTurnId: "running"), CancellationToken.None);

        Assert.Null(retried.Replay);
        Assert.NotNull(retried.PendingTurn);
        Assert.Contains("still being processed", stillRunning.Replay!.Reply);
    }

    [Fact]
    public async Task Memory_ReplaysAPastTurnsLookupsIntoTheNextPromptButNeverForSensitiveTurns()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { HideSensitive = false });
        await context.SaveChangesAsync();
        var memory = new AiConversationMemoryService(context);
        var trace = new[] { new FinancialAppApi.Services.AI.Agent.AiToolTrace("search_transactions", """{"query":"haircut"}""", true) };

        var first = await memory.PrepareAsync(new AiChatRequest("when was my last haircut?", [], ClientTurnId: "t1"), CancellationToken.None);
        await memory.CompleteAsync(first, "when was my last haircut?", new AiChatResponse("On 14 Feb 2026.", []), CancellationToken.None, trace);
        var second = await memory.PrepareAsync(new AiChatRequest("and before that?", [], ClientTurnId: "t2"), CancellationToken.None);

        var assistant = second.History.Single(message => message.Role == "assistant").Content;
        Assert.StartsWith("On 14 Feb 2026.", assistant);
        Assert.Contains("[Looked up: search_transactions {\"query\":\"haircut\"}]", assistant);
        var hydrated = await memory.GetActiveAsync();
        Assert.DoesNotContain(hydrated.Messages, message => message.Content.Contains("Looked up"));

        var hidden = await memory.PrepareAsync(new AiChatRequest("secret", [], ClientTurnId: "t3", ForceSensitiveMode: true), CancellationToken.None);
        await memory.CompleteAsync(hidden, "secret", new AiChatResponse("Hidden.", []), CancellationToken.None, trace);
        Assert.Null((await context.AiConversationTurns.SingleAsync(turn => turn.ClientTurnId == "t3")).ToolTraceJson);
    }

    [Fact]
    public async Task UsageMeter_AccumulatesToday()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var meter = new AiUsageMeter(context);

        await meter.RecordAsync(new AiTokenUsage(400, 300, 100, 20), calls: 2, CancellationToken.None);
        await meter.RecordAsync(new AiTokenUsage(450, 0, 60, 0), calls: 1, CancellationToken.None);

        var day = await context.AiUsageDays.SingleAsync();
        Assert.Equal(850, day.InputTokens);
        Assert.Equal(300, day.CachedTokens);
        Assert.Equal(160, day.OutputTokens);
        Assert.Equal(3, day.Calls);
    }

    private static AiConversationTurn Turn(AiConversation conversation, string clientTurnId, string status, DateTime createdAt) => new()
    {
        UserId = TestHelpers.DefaultUserId,
        Conversation = conversation,
        ClientTurnId = clientTurnId,
        UserMessage = "question",
        AssistantReply = status == "Completed" ? "answer" : string.Empty,
        Status = status,
        CreatedAt = createdAt
    };

    private static AppDbContext NewSqliteContext(SqliteConnection connection)
    {
        var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        context.SetCurrentUser(TestHelpers.DefaultUserId);
        return context;
    }

    private static LedgerRetentionService NewRetention(AppDbContext context)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Retention:Enabled"] = "true", ["Financial:TimeZoneId"] = "UTC" })
            .Build();
        return new LedgerRetentionService(context, new LedgerRetentionPolicy(configuration), new FinancialClock(configuration));
    }
}
