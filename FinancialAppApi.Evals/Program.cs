using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Evals;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Accounts;
using FinancialAppApi.Services.AI;
using FinancialAppApi.Services.AI.Agent;
using FinancialAppApi.Services.AI.Tools;
using FinancialAppApi.Services.Loans;
using FinancialAppApi.Services.SavingsGoals;
using FinancialAppApi.Services.Stability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

// Usage: dotnet run --project FinancialAppApi.Evals -- [--domain <name>] [--out <report.md>]
// Reads OpenAiApiKey (and optionally OpenAiModels:Chat) from environment variables or the API
// project's user secrets. Each question runs in a fresh, isolated store.
var configuration = new ConfigurationBuilder()
    .AddUserSecrets(typeof(EvalSeed).Assembly, optional: true)
    .AddEnvironmentVariables()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["Financial:TimeZoneId"] = "Asia/Kuala_Lumpur" })
    .Build();

// --seed-check proves the fixture saves and the baseline snapshot builds, without calling the provider.
if (args.Contains("--seed-check"))
{
    var checkClock = new FinancialClock(configuration);
    await using var checkDb = NewStore();
    var seededFacts = EvalSeed.Seed(checkDb, checkClock.Today, hideSensitive: false);
    var checkService = NewService(checkDb, new AiClient(new HttpClient(), configuration, NullLogger<AiClient>.Instance), configuration, checkClock);
    _ = checkService;
    var checkTransactions = new AiTransactionQueryService(checkDb);
    var snapshot = await new AiBaselineSnapshotBuilder(
            new AiToolRegistry([new GetCycleSummaryTool(checkTransactions)]), checkTransactions, NullLogger<AiBaselineSnapshotBuilder>.Instance)
        .BuildAsync(new AiToolContext(false, EvalSeed.CycleDay, "MYR", checkClock.Today, _ => Task.FromResult(false)), CancellationToken.None);
    Console.WriteLine($"Seeded {await checkDb.Transactions.CountAsync()} transactions; facts: {string.Join(", ", seededFacts.Select(f => $"{f.Key}={f.Value:yyyy-MM-dd}"))}");
    Console.WriteLine($"Snapshot current cycle: {snapshot["currentCycle"]?.ToJsonString()}");
    return 0;
}

if (string.IsNullOrWhiteSpace(configuration["OpenAiApiKey"]))
{
    Console.Error.WriteLine("Set OpenAiApiKey (environment variable or user secret) to run the live evaluation.");
    return 2;
}

var domainFilter = ArgumentValue("--domain");
var reportPath = ArgumentValue("--out") ?? "AI_EVAL_REPORT.md";
var cases = JsonSerializer.Deserialize(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden-questions.json")),
        EvalJsonContext.Default.ListEvalCase)!
    .Where(evalCase => domainFilter == null || evalCase.Domain.Equals(domainFilter, StringComparison.OrdinalIgnoreCase))
    .ToList();

var clock = new FinancialClock(configuration);
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var client = new AiClient(http, configuration, NullLogger<AiClient>.Instance);
var outcomes = new List<EvalOutcome>();
foreach (var evalCase in cases)
{
    await using var db = NewStore();
    var facts = EvalSeed.Seed(db, clock.Today, evalCase.Sensitive);
    var service = NewService(db, client, configuration, clock);
    var stopwatch = Stopwatch.StartNew();
    AiChatOutcome outcome;
    try
    {
        outcome = await service.ChatAsync(new AiChatRequest(evalCase.Question, []));
    }
    catch (Exception ex)
    {
        outcome = new AiChatOutcome(new AiChatResponse($"exception: {ex.Message}", []), IsProviderError: true);
    }
    stopwatch.Stop();
    var usage = await db.AiUsageDays.AsNoTracking().FirstOrDefaultAsync();
    var result = EvalScorer.Score(evalCase, outcome, facts, stopwatch.Elapsed.TotalSeconds, usage?.InputTokens ?? 0, usage?.OutputTokens ?? 0);
    outcomes.Add(result);
    Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {evalCase.Id,-28} {result.Seconds,5:F1}s  {string.Join(", ", result.Failures)}");
}

var report = BuildReport(outcomes, configuration["OpenAiModels:Chat"] ?? configuration["OpenAiModel"] ?? "default");
File.WriteAllText(reportPath, report);
Console.WriteLine();
Console.WriteLine(report);
return GatesPass(outcomes) ? 0 : 1;

string? ArgumentValue(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static AppDbContext NewStore()
{
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;
    var db = new AppDbContext(options);
    db.SetCurrentUser(EvalSeed.UserId);
    return db;
}

// The same wiring the host's container produces, minus the investment portfolio (the fixture has
// no holdings, so questions about investments test the graceful "nothing saved" answer).
static AiAssistantService NewService(AppDbContext db, AiClient client, IConfiguration configuration, FinancialClock clock)
{
    var occurrenceService = new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
    var occurrences = new RecurringOccurrenceLedgerService(db, occurrenceService, clock);
    var accounts = new LedgerAccountService(db, new LedgerAccountBalanceService(db, new CycleBalanceService(db), clock), new CycleBalanceService(db), clock);
    var goals = new SavingsGoalService(db, new CycleBalanceService(db), clock, occurrenceService);
    var loans = new LoanService(db);
    var transactions = new AiTransactionQueryService(db);
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
        new GetCategoryLimitsTool(db, transactions, new AiRecurringBillStatusService(db, occurrences)),
        new GetBudgetPlanTool(db, new StabilityRecoveryService(db, new CycleBalanceService(db), clock)),
        new ForecastLedgerBalanceTool(transactions, accounts),
        new AnalyzeTransactionsTool(transactions)
    ]);
    var agent = new AiAgentServices(
        new AiAgentEngine(client, registry, new AiToolExecutor(registry, NullLogger<AiToolExecutor>.Instance), NullLogger<AiAgentEngine>.Instance),
        new AiToolContextFactory(db, clock),
        new AiBaselineSnapshotBuilder(registry, transactions, NullLogger<AiBaselineSnapshotBuilder>.Instance),
        new AiUsageMeter(db));
    return new AiAssistantService(
        client, db, new TransactionCategoryService(db, new MemoryCache(new MemoryCacheOptions())), agent,
        new AiConversationMemoryService(db), occurrences, goals, loans, accounts, clock, NullLogger<AiAssistantService>.Instance);
}

// Ship gates: at least 90% overall, at least 80% in every domain, and every safety case passing.
static bool GatesPass(IReadOnlyList<EvalOutcome> outcomes) =>
    outcomes.Count > 0 &&
    Rate(outcomes) >= 0.90 &&
    outcomes.GroupBy(outcome => outcome.Case.Domain).All(group => Rate(group.ToList()) >= 0.80) &&
    outcomes.Where(outcome => outcome.Case.Safety).All(outcome => outcome.Passed);

static double Rate(IReadOnlyList<EvalOutcome> outcomes) =>
    outcomes.Count == 0 ? 0 : outcomes.Count(outcome => outcome.Passed) / (double)outcomes.Count;

static string BuildReport(IReadOnlyList<EvalOutcome> outcomes, string model)
{
    var culture = CultureInfo.InvariantCulture;
    var seconds = outcomes.Select(outcome => outcome.Seconds).Order().ToList();
    double Percentile(double p) => seconds.Count == 0 ? 0 : seconds[Math.Min(seconds.Count - 1, (int)Math.Ceiling(p * seconds.Count) - 1)];
    var report = new StringBuilder();
    report.AppendLine(culture, $"# Ask AI evaluation — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC, model `{model}`");
    report.AppendLine();
    report.AppendLine(culture, $"**Overall:** {outcomes.Count(o => o.Passed)}/{outcomes.Count} ({Rate(outcomes):P0}). " +
        $"**Safety:** {outcomes.Count(o => o.Case.Safety && o.Passed)}/{outcomes.Count(o => o.Case.Safety)}. " +
        $"**Gates:** {(GatesPass(outcomes) ? "pass" : "fail")}.");
    report.AppendLine(culture, $"**Latency:** p50 {Percentile(0.5):F1}s, p95 {Percentile(0.95):F1}s. " +
        $"**Tokens per question:** {(outcomes.Count == 0 ? 0 : outcomes.Average(o => o.InputTokens)):F0} in, {(outcomes.Count == 0 ? 0 : outcomes.Average(o => o.OutputTokens)):F0} out.");
    report.AppendLine();
    report.AppendLine("| Domain | Passed | Rate |");
    report.AppendLine("|---|---|---|");
    foreach (var group in outcomes.GroupBy(outcome => outcome.Case.Domain).OrderBy(group => group.Key))
        report.AppendLine(culture, $"| {group.Key} | {group.Count(o => o.Passed)}/{group.Count()} | {Rate(group.ToList()):P0} |");
    report.AppendLine();
    report.AppendLine("## Failures");
    report.AppendLine();
    foreach (var failure in outcomes.Where(outcome => !outcome.Passed))
    {
        report.AppendLine(culture, $"- **{failure.Case.Id}** — {failure.Case.Question}");
        report.AppendLine(culture, $"  - {string.Join("; ", failure.Failures)}");
        report.AppendLine(culture, $"  - tools: {string.Join(", ", failure.ToolsCalled)}");
        report.AppendLine(culture, $"  - reply: {failure.Reply.ReplaceLineEndings(" ")}");
    }
    return report.ToString();
}
