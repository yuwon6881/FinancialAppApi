using System.Net;
using System.Text;
using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Accounts;
using FinancialAppApi.Services.AI;
using FinancialAppApi.Services.AI.Agent;
using FinancialAppApi.Services.AI.Tools;
using FinancialAppApi.Services.Loans;
using FinancialAppApi.Services.SavingsGoals;
using FinancialAppApi.Services.Stability;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests.AiTools;

// Wires the tool-calling chat engine over an InMemory store and a scripted provider, the same
// way the host's DI container does, with a fixed clock (2026-09-29, cycle day 25).
internal sealed class AgentHarness : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public AgentHarness(
        AppDbContext db,
        ScriptedProvider provider,
        long dailyTokenBudget = AiUsageMeter.DefaultDailyTokenBudget,
        bool configured = true)
    {
        Db = db;
        Provider = provider;
        var configuration = TestHelpers.NewConfiguration(
            ("OpenAiApiKey", configured ? "key" : ""), ("OpenAiModel", "test-model"), ("Financial:TimeZoneId", "UTC"),
            ("Ai:DailyTokenBudgetPerUser", dailyTokenBudget.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var clock = new FinancialClock(configuration, new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));
        var client = new AiClient(new HttpClient(provider), configuration, NullLogger<AiClient>.Instance);
        var occurrenceService = new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        var occurrences = new RecurringOccurrenceLedgerService(db, occurrenceService, clock);
        var accounts = new LedgerAccountService(db, new LedgerAccountBalanceService(db, new CycleBalanceService(db), clock), new CycleBalanceService(db), clock);
        var goals = new SavingsGoalService(db, new CycleBalanceService(db), clock, occurrenceService);
        var loans = new LoanService(db);
        var transactions = new AiTransactionQueryService(db);
        var bills = new AiRecurringBillStatusService(db, occurrences);
        var registry = new AiToolRegistry(
        [
            new SearchTransactionsTool(transactions),
            new GetCycleSummaryTool(transactions),
            new CompareCyclesTool(transactions),
            new GetSpendingBreakdownTool(transactions, accounts),
            new GetPurchasePatternTool(transactions),
            new GetAccountsTool(accounts),
            new GetRecurringTool(db, occurrences),
            new GetLoansTool(loans),
            new GetGoalsAndRewardsTool(db, goals, transactions),
            new GetCategoryLimitsTool(db, transactions, bills),
            new GetBudgetPlanTool(db, new StabilityRecoveryService(db, new CycleBalanceService(db), clock)),
            new ForecastLedgerBalanceTool(transactions, accounts),
            new AnalyzeTransactionsTool(transactions)
        ]);
        var agent = new AiAgentServices(
            new AiAgentEngine(client, registry, new AiToolExecutor(registry, NullLogger<AiToolExecutor>.Instance), NullLogger<AiAgentEngine>.Instance),
            new AiToolContextFactory(db, clock),
            new AiBaselineSnapshotBuilder(registry, transactions, NullLogger<AiBaselineSnapshotBuilder>.Instance),
            new AiUsageMeter(db, configuration));
        Service = new AiAssistantService(
            client, db, new TransactionCategoryService(db, _cache), agent, new AiConversationMemoryService(db),
            occurrences, goals, loans, accounts, clock, NullLogger<AiAssistantService>.Instance);
    }

    public AppDbContext Db { get; }
    public ScriptedProvider Provider { get; }
    public AiAssistantService Service { get; }

    public Task<AiChatOutcome> AskAsync(string message, AiInvocationContext? context = null, AiConversationState? state = null) =>
        Service.ChatAsync(new AiChatRequest(message, [], state, Context: context));

    public Task<AiChatOutcome> AskAsync(AiChatRequest request) => Service.ChatAsync(request);

    // The developer message the engine sent on its first round (snapshot, mentions, pending request).
    public string FirstDeveloperMessage() =>
        Provider.Requests[0].GetProperty("input").EnumerateArray()
            .First(item => item.TryGetProperty("role", out var role) && role.GetString() == "developer")
            .GetProperty("content").GetString()!;

    public void Dispose() => _cache.Dispose();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

// Returns queued Responses API bodies in order and records every request body.
internal sealed class ScriptedProvider : HttpMessageHandler
{
    private readonly Queue<string> _responses = new();

    public List<JsonElement> Requests { get; } = [];

    // When set, every request fails with this status instead of reading the script.
    public HttpStatusCode? AlwaysFailWith { get; set; }

    // A proposal of these actions: [{ type, payload }].
    public ScriptedProvider Propose(params object[] actions) => Call("propose_ui_actions", new { actions });

    public ScriptedProvider Call(string tool, object arguments, string callId = "")
    {
        var id = callId.Length > 0 ? callId : $"call_{_responses.Count}";
        _responses.Enqueue(Body($$"""{"type":"function_call","call_id":"{{id}}","name":"{{tool}}","arguments":{{JsonSerializer.Serialize(JsonSerializer.Serialize(arguments))}}}"""));
        return this;
    }

    public ScriptedProvider Answer(string text)
    {
        _responses.Enqueue(Body($$"""{"type":"message","role":"assistant","content":[{"type":"output_text","text":{{JsonSerializer.Serialize(text)}}}]}"""));
        return this;
    }

    // The function_call_output the engine sent back for a call id, parsed.
    public JsonElement OutputFor(string callId)
    {
        foreach (var request in Requests)
        foreach (var item in request.GetProperty("input").EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output" &&
                item.GetProperty("call_id").GetString() == callId)
                return JsonDocument.Parse(item.GetProperty("output").GetString()!).RootElement.Clone();
        }
        throw new InvalidOperationException($"No output was sent for {callId}.");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone());
        if (AlwaysFailWith is { } status) return new HttpResponseMessage(status) { Content = new StringContent("{}") };
        if (_responses.Count == 0) throw new InvalidOperationException("The script ran out of provider responses.");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json") };
    }

    private static string Body(string item) =>
        $$$"""{"status":"completed","output":[{{{item}}}],"usage":{"input_tokens":100,"output_tokens":10}}""";
}
