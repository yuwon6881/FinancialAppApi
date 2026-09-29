using System.Text.Json.Nodes;
using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.AI.Tools;

public sealed class GetCategoryLimitsTool : IAiTool
{
    private readonly AppDbContext _context;
    private readonly AiTransactionQueryService _transactions;
    private readonly AiRecurringBillStatusService _bills;

    public GetCategoryLimitsTool(AppDbContext context, AiTransactionQueryService transactions, AiRecurringBillStatusService bills)
    {
        _context = context;
        _transactions = transactions;
        _bills = bills;
    }

    public string Name => "get_category_limits";

    public string Description =>
        "Progress against the user's category spending limits for a cycle (default current): limit, spent so far, " +
        "remaining, bills still due in that category, projected end-of-cycle spend, and status OnTrack, Watch " +
        "(projected to go over), or Exceeded. For an unfinished cycle, projected figures are forecasts.";

    public JsonObject ParametersSchema { get; } = AiToolSchema.Object(
    [
        ("cycleKey", AiToolSchema.String("\"current\" (default), \"previous\", or a cycle key like \"2026-08\"."))
    ]);

    public AiToolSensitivity Sensitivity => AiToolSensitivity.MasksAmounts;

    public IReadOnlySet<string> AmountProperties { get; } = new HashSet<string>(StringComparer.Ordinal) { "percentUsed" };

    public string ProgressLabel(AiToolArgs args) => "Checking your category limits";

    public async Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken)
    {
        var cycle = AiCycleResolver.Parse(args.OptionalString("cycleKey", 16) ?? "current", context.Today, context.CycleDay);
        var guides = await _context.CategorySpendingGuides.AsNoTracking().ToListAsync(cancellationToken);
        if (guides.Count == 0)
            return AiToolResult.Of(new { cycle = cycle.Key, limits = Array.Empty<object>(), note = "The user has not set any category limits." });

        var categoryTypes = (await _context.TransactionCategories
                .AsNoTracking()
                .Select(category => new { category.Name, category.Type })
                .ToListAsync(cancellationToken))
            .GroupBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Type, StringComparer.OrdinalIgnoreCase);
        var (rows, truncated) = await _transactions.LoadAsync(
            _transactions.Scoped(new AiTransactionFilter([AiCycleResolver.Range(cycle, context.CycleDay)], TxType: "outflow")),
            GetCycleSummaryTool.RowCap,
            cancellationToken);
        var bills = await _bills.LoadAsync([cycle], context.CycleDay, cancellationToken);

        var limits = AiCategoryLimitCalculator.Compute(
            guides,
            categoryTypes,
            bills,
            [cycle],
            context.CycleDay,
            context.Today,
            rows.Select(row => new AiSpendRow(row.Date, row.Category, row.LedgerCategory, row.Amount, row.RecurringPaymentId)).ToList());
        return AiToolResult.Of(new
        {
            cycle = cycle.Key,
            limits,
            note = limits.Count == 0 ? "No category limit applies to this cycle." : null
        }, truncated: truncated, approximate: truncated);
    }
}
