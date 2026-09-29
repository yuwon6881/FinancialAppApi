using System.Text.Json.Nodes;
using FinancialAppApi.Services.AI;
using FinancialAppApi.Services.AI.Tools;
using static FinancialAppApi.Tests.AiTools.AiToolTestSupport;

namespace FinancialAppApi.Tests.AiTools;

public class AiToolFrameworkTests
{
    [Fact]
    public void Registry_RejectsDuplicateNamesAndEmitsDefinitionsInStableOrder()
    {
        Assert.Throws<InvalidOperationException>(() => new AiToolRegistry([new FakeTool("a"), new FakeTool("a")]));

        var registry = new AiToolRegistry([new FakeTool("zeta"), new FakeTool("alpha"), new FakeTool("mid")]);

        Assert.Equal(["alpha", "mid", "zeta"], registry.Definitions.Select(definition => definition.Name));
    }

    [Fact]
    public async Task Executor_ReportsUnknownToolAndMalformedArgumentsAsModelReadableErrors()
    {
        var executor = NewExecutor(new FakeTool("known"));
        var context = NewContext();

        var unknown = Parse(await RunAsync(executor, context, "missing"));
        var malformed = Parse(await RunAsync(executor, context, "known", "{not json"));

        Assert.False(unknown.GetProperty("ok").GetBoolean());
        Assert.Contains("known", unknown.GetProperty("error").GetString());
        Assert.False(malformed.GetProperty("ok").GetBoolean());
        Assert.Equal("Arguments were not valid JSON.", malformed.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Executor_TurnsToolArgumentMistakesAndServerFailuresIntoErrorsWithoutThrowing()
    {
        var executor = NewExecutor(
            new FakeTool("picky", execute: (_, _) => throw new AiToolArgumentException("cycle must be current")),
            new FakeTool("broken", execute: (_, _) => throw new InvalidOperationException("db down")));
        var context = NewContext();

        var picky = Parse(await RunAsync(executor, context, "picky"));
        var broken = Parse(await RunAsync(executor, context, "broken"));

        Assert.Equal("cycle must be current", picky.GetProperty("error").GetString());
        Assert.DoesNotContain("db down", broken.GetProperty("error").GetString());
        Assert.Contains("could not be checked", broken.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Executor_ReturnsTheFirstAnswerForAnIdenticalRepeatedCallWithoutRunningAgain()
    {
        var runs = 0;
        var executor = NewExecutor(new FakeTool("count", execute: (_, _) => AiToolResult.Of(new { run = ++runs })));
        var context = NewContext();

        await RunAsync(executor, context, "count", """{"a":1}""");
        var repeated = Parse(await RunAsync(executor, context, "count", """{"a":1}"""));
        await RunAsync(executor, context, "count", """{"a":2}""");

        Assert.Equal(1, repeated.GetProperty("data").GetProperty("run").GetInt32());
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Executor_StopsRunningToolsOnceTheCallBudgetIsSpent()
    {
        var runs = 0;
        var executor = NewExecutor(new FakeTool("count", execute: (_, _) => AiToolResult.Of(new { run = ++runs })));
        var context = NewContext(budget: new AiTurnBudget(maxToolCalls: 2));

        await RunAsync(executor, context, "count", """{"a":1}""");
        await RunAsync(executor, context, "count", """{"a":2}""");
        var third = Parse(await RunAsync(executor, context, "count", """{"a":3}"""));

        Assert.Equal(2, runs);
        Assert.False(third.GetProperty("ok").GetBoolean());
        Assert.Contains("budget", third.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Executor_RejectsAnOversizedResultAndAsksTheModelToNarrow()
    {
        var executor = NewExecutor(new FakeTool("huge", execute: (_, _) => AiToolResult.Of(new { text = new string('x', 500) })));
        var context = NewContext(budget: new AiTurnBudget(maxCharactersPerResult: 100));

        var result = Parse(await RunAsync(executor, context, "huge"));

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("Narrow", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Executor_DoesNotRunHiddenToolsInSensitiveMode()
    {
        var runs = 0;
        var executor = NewExecutor(new FakeTool("portfolio", AiToolSensitivity.HiddenWhenSensitive,
            (_, _) => AiToolResult.Of(new { value = ++runs })));

        var result = Parse(await RunAsync(executor, NewContext(sensitive: true), "portfolio"));

        Assert.Equal(0, runs);
        Assert.Contains("sensitive mode", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Executor_StripsMoneyAtEveryDepthIncludingToolDeclaredNames()
    {
        var executor = NewExecutor(new FakeTool("rows", AiToolSensitivity.MasksAmounts,
            (_, _) => AiToolResult.Of(new
            {
                totalOutflow = 90m,
                rows = new[] { new { description = "Haircut", amount = -45m, fee = 2m } }
            }),
            amountProperties: new HashSet<string> { "fee" }));

        var result = Parse(await RunAsync(executor, NewContext(sensitive: true), "rows"));

        var data = result.GetProperty("data");
        Assert.True(result.GetProperty("sensitiveMode").GetBoolean());
        Assert.False(data.TryGetProperty("totalOutflow", out _));
        var row = data.GetProperty("rows")[0];
        Assert.Equal("Haircut", row.GetProperty("description").GetString());
        Assert.False(row.TryGetProperty("amount", out _));
        Assert.False(row.TryGetProperty("fee", out _));
    }

    [Fact]
    public async Task Executor_MasksWhenSensitiveModeSwitchesOnDuringTheTurn()
    {
        var hidden = false;
        var executor = NewExecutor(new FakeTool("rows", AiToolSensitivity.MasksAmounts,
            (_, _) =>
            {
                hidden = true; // the user hides balances while the query is in flight
                return AiToolResult.Of(new { amount = 12m });
            }));
        var context = NewContext(sensitive: false, sensitiveSetting: () => hidden);

        var result = Parse(await RunAsync(executor, context, "rows"));

        Assert.True(context.SensitiveMode);
        Assert.False(result.GetProperty("data").TryGetProperty("amount", out _));
    }

    [Fact]
    public void Args_TreatNullAsAbsentAndBoundEveryValue()
    {
        var args = AiToolArgs.Parse("""{"query":null,"limit":5,"sort":"NEWEST","date":"2026-02-30"}""");

        Assert.Null(args.OptionalString("query"));
        Assert.Equal(5, args.OptionalInt("limit", 1, 50));
        Assert.Equal("newest", args.OptionalEnum("sort", ["newest", "oldest"]));
        Assert.Throws<AiToolArgumentException>(() => args.OptionalInt("limit", 1, 3));
        Assert.Throws<AiToolArgumentException>(() => args.OptionalDate("date"));
        Assert.Throws<AiToolArgumentException>(() => AiToolArgs.Parse("[1,2]"));
    }

    [Fact]
    public void CycleResolver_ParsesWordsAndKeysAndNamesValidValuesOnError()
    {
        Assert.Equal("2026-09", AiCycleResolver.Parse("current", Today, CycleDay).Key);
        Assert.Equal("2026-08", AiCycleResolver.Parse("previous", Today, CycleDay).Key);
        Assert.Equal("2025-12", AiCycleResolver.Parse("2025-12", Today, CycleDay).Key);
        var error = Assert.Throws<AiToolArgumentException>(() => AiCycleResolver.Parse("2026-13", Today, CycleDay));
        Assert.Contains("2026-09", error.Message);
    }

    [Fact]
    public void CycleResolver_UsesCycleDayBoundariesAndMergesAdjacentCycles()
    {
        var september = AiCycleResolver.Range(new AiCycle(2026, 9), CycleDay);
        Assert.Equal(new DateOnly(2026, 9, 25), september.FirstDate);
        Assert.Equal(new DateOnly(2026, 10, 24), september.LastDate);
        // A date early in October still belongs to the cycle labelled September.
        Assert.Equal("2026-09", AiCycleResolver.CycleOf(new DateOnly(2026, 10, 3), CycleDay).Key);

        var merged = AiCycleResolver.MergedRanges([new AiCycle(2026, 7), new AiCycle(2026, 8), new AiCycle(2026, 5)], CycleDay);
        Assert.Equal(2, merged.Count);
        Assert.Equal(new DateOnly(2026, 7, 25), merged[1].FirstDate);
        Assert.Equal(new DateOnly(2026, 9, 24), merged[1].LastDate);
    }

    private sealed class FakeTool(
        string name,
        AiToolSensitivity sensitivity = AiToolSensitivity.None,
        Func<AiToolArgs, AiToolContext, AiToolResult>? execute = null,
        IReadOnlySet<string>? amountProperties = null) : IAiTool
    {
        public string Name => name;
        public string Description => "fake";
        public JsonObject ParametersSchema { get; } = AiToolSchema.Object([]);
        public AiToolSensitivity Sensitivity => sensitivity;
        public IReadOnlySet<string> AmountProperties => amountProperties ?? new HashSet<string>();
        public string ProgressLabel(AiToolArgs args) => "Working";

        public Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult(execute?.Invoke(args, context) ?? AiToolResult.Of(new { ok = true }));
    }
}
