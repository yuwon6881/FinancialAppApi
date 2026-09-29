using System.Text.Json.Nodes;
using FinancialAppApi.Services.Accounts;

namespace FinancialAppApi.Services.AI.Tools;

// "How long until my Growth reaches 50,000?" The current bucket balance is the sum of its
// accounts (a bucket's balance is derived from its ledger accounts); the savings rate is the
// average positive amount added across recent completed cycles that had activity.
public sealed class ForecastLedgerBalanceTool : IAiTool
{
    private const int RateCycles = 6;
    private static readonly string[] Buckets = ["Essentials", "Growth", "Stability", "Rewards"];

    private readonly AiTransactionQueryService _transactions;
    private readonly LedgerAccountService _accounts;

    public ForecastLedgerBalanceTool(AiTransactionQueryService transactions, LedgerAccountService accounts)
    {
        _transactions = transactions;
        _accounts = accounts;
    }

    public string Name => "forecast_ledger_balance";

    public string Description =>
        "Estimate when a ledger bucket (Essentials, Growth, Stability, Rewards) will reach a target amount, from its " +
        "current balance and the average it gained per cycle over the last six completed cycles. Returns the estimate " +
        "with its status and assumptions; quote them rather than doing your own arithmetic.";

    public JsonObject ParametersSchema { get; } = AiToolSchema.Object(
    [
        ("ledgerCategory", AiToolSchema.Enum("Bucket to project.", Buckets)),
        ("targetAmount", AiToolSchema.Number("Balance to reach.", 0))
    ], "ledgerCategory", "targetAmount");

    public AiToolSensitivity Sensitivity => AiToolSensitivity.HiddenWhenSensitive;

    public string ProgressLabel(AiToolArgs args) => "Projecting your balance";

    public async Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken)
    {
        var bucket = args.OptionalEnum("ledgerCategory", Buckets) ?? throw new AiToolArgumentException("ledgerCategory is required.");
        var target = args.OptionalDecimal("targetAmount", 0.01m, 1_000_000_000m) ?? throw new AiToolArgumentException("targetAmount is required.");

        var accounts = (await _accounts.GetAccountsAsync(cancellationToken)).Where(account => account.Bucket == bucket).ToList();
        var balances = accounts.Count == 0
            ? new Dictionary<string, decimal>()
            : await _accounts.GetBalancesAsync(accounts, cancellationToken);
        var current = accounts.Sum(account => balances.GetValueOrDefault(account.Id));

        var currentCycle = AiCycleResolver.Current(context.Today, context.CycleDay);
        var cycles = Enumerable.Range(1, RateCycles).Select(offset => currentCycle.AddCycles(-offset)).ToList();
        var (rows, truncated) = await _transactions.LoadAsync(
            _transactions.Scoped(new AiTransactionFilter(AiCycleResolver.MergedRanges(cycles, context.CycleDay))),
            GetCycleSummaryTool.RowCap * RateCycles,
            cancellationToken);

        var result = AiAssistantService.ComputeLedgerBalanceForecast(
            bucket, target, current, AiLegacyRows.From(rows), AiLegacyRows.From(cycles), context.CycleDay,
            context.Today.ToDateTime(TimeOnly.MinValue));
        return AiToolResult.Of(new
        {
            ledgerCategory = result.LedgerCategory,
            targetAmount = result.Target,
            currentBalance = result.CurrentBalance,
            remaining = result.Remaining,
            savingsPerCycle = result.SavingsPerCycle,
            estimatedCycles = result.EstimatedCycles,
            estimatedDate = result.EstimatedDate,
            status = result.Status,
            cycleSavings = result.CycleSavings,
            assumption = result.Assumption
        }, truncated: truncated, approximate: truncated);
    }
}
