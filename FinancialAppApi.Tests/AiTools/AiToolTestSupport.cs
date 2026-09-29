using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.AI.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests.AiTools;

internal static class AiToolTestSupport
{
    // 2026-09-29 with cycle day 25: the current cycle is "2026-09" (Sep 25 - Oct 24).
    public static readonly DateOnly Today = new(2026, 9, 29);
    public const int CycleDay = 25;

    public static AiToolContext NewContext(bool sensitive = false, Func<bool>? sensitiveSetting = null, AiTurnBudget? budget = null) =>
        new(sensitive, CycleDay, "MYR", Today,
            _ => Task.FromResult(sensitiveSetting?.Invoke() ?? sensitive),
            budget);

    public static AiToolExecutor NewExecutor(params IAiTool[] tools) =>
        new(new AiToolRegistry(tools), NullLogger<AiToolExecutor>.Instance);

    public static Task<AiToolExecution> RunAsync(
        AiToolExecutor executor,
        AiToolContext context,
        string tool,
        string arguments = "{}") =>
        executor.ExecuteAsync(new AiFunctionCall("call", tool, arguments), context, CancellationToken.None);

    public static JsonElement Parse(AiToolExecution execution) =>
        JsonDocument.Parse(execution.Output).RootElement.Clone();

    // Several contexts over one InMemory store, one per user, to prove tenant isolation.
    public static Func<string, AppDbContext> SharedStore()
    {
        var name = Guid.NewGuid().ToString();
        return userId =>
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(name)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var context = new AppDbContext(options);
            context.SetCurrentUser(userId);
            return context;
        };
    }

    public static Transaction Txn(
        string id,
        DateOnly date,
        string description,
        decimal amount,
        string category = "Personal Care",
        string ledgerCategory = "Essentials") => new()
    {
        Id = id,
        Date = TransactionDate.StartOfDate(date).AddHours(12),
        Description = description,
        Category = category,
        LedgerCategory = ledgerCategory,
        Amount = amount
    };
}
