using System.Text.Json.Nodes;
using FinancialAppApi.Services.Forecast;

namespace FinancialAppApi.Services.AI.Tools;

// The Forecast workspace, read by Ask AI. The scenario goes to the same CashFlowForecastService the
// workspace calls, so an answer here matches what the user sees there; nothing is projected twice
// and nothing is stored.
public sealed class ForecastCashFlowTool(CashFlowForecastService forecast) : IAiTool
{
    private const int DefaultCycles = 4;
    // Cycle totals are always returned; individual lines are capped to keep the result readable.
    private const int MaxLines = 40;

    public string Name => "forecast_cash_flow";

    public string Description =>
        "Run the app's Forecast (the same projection as the Forecast screen): each bucket's balance (Essentials, Growth, " +
        "Stability, Rewards) from today through the rest of this cycle and the next N cycles (default 4, max 12), taking " +
        "every upcoming recurring bill and loan instalment from the user's records. Optional what-ifs: incomePerCycle " +
        "(split like a salary on each payday, Stability target cap included; omitted means income is unknown, so later " +
        "balances show bills only), everydaySpendingPerCycle (extra Essentials spending), whatIfs (dated one-off " +
        "spending from a bucket as a negative amount, money added to a bucket as positive, or extra income with no " +
        "bucket), and skipBills (billKey values from a previous result). Use for \"will I run short\", \"what will my " +
        "balance be next month\", \"can I afford X on a date\" and \"what if I spend/earn X\". For how long a bucket takes " +
        "to reach a target amount, use forecast_ledger_balance. Quote its figures; never recompute them, and say which " +
        "assumptions were included.";

    public JsonObject ParametersSchema { get; } = AiToolSchema.Object(
    [
        ("cycles", AiToolSchema.Integer("Whole cycles after the current one.", CashFlowForecastService.MinCycles, CashFlowForecastService.MaxCycles)),
        ("incomePerCycle", AiToolSchema.Number("Income arriving each payday. Omit when the user gave none.", 0)),
        ("everydaySpendingPerCycle", AiToolSchema.Number("Everyday Essentials spending per cycle beyond bills.", 0)),
        ("whatIfs", AiToolSchema.ObjectArray("Temporary dated changes.",
        [
            ("date", AiToolSchema.Date("When it happens.")),
            ("amount", new JsonObject { ["type"] = "number", ["description"] = "Negative takes money out, positive adds it." }),
            ("bucket", AiToolSchema.Enum("Bucket affected; omit for extra income split like a salary.", [.. ForecastBuckets.All])),
            ("label", AiToolSchema.String("Short name, up to 80 characters."))
        ], ["date", "amount", "label"], CashFlowForecastService.MaxItems)),
        ("skipBills", AiToolSchema.StringArray("billKey values of upcoming bills to leave out.", 50))
    ]);

    public AiToolSensitivity Sensitivity => AiToolSensitivity.HiddenWhenSensitive;

    public string ProgressLabel(AiToolArgs args) => "Running your forecast";

    public async Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken)
    {
        var scenario = new CashFlowScenario(
            args.OptionalInt("cycles", CashFlowForecastService.MinCycles, CashFlowForecastService.MaxCycles) ?? DefaultCycles,
            args.OptionalDecimal("incomePerCycle", 0m, CashFlowForecastService.MaxAmount),
            args.OptionalDecimal("everydaySpendingPerCycle", 0m, CashFlowForecastService.MaxAmount),
            args.OptionalObjectArray("whatIfs", CashFlowForecastService.MaxItems).Select(ParseItem).ToList(),
            args.OptionalStringArray("skipBills", 50, 200));
        // The workspace's own rules, so the model is told exactly what the screen would reject.
        var error = CashFlowForecastService.Validate(scenario);
        if (error is not null) throw new AiToolArgumentException(error);

        var response = await forecast.ForecastAsync(scenario, cancellationToken);
        var result = response.Forecast;
        var lineBudget = MaxLines;
        var truncated = false;

        var cycles = result.Cycles.Select(cycle =>
        {
            var lines = Collapse(cycle.Lines);
            var shown = lines.Take(Math.Max(lineBudget, 0)).ToList();
            lineBudget -= shown.Count;
            truncated |= shown.Count < lines.Count;
            context.Evidence.RecordAll(AiEvidenceLedger.Recurring, shown.Select(line => PaymentId(line.SourceId)));
            return new
            {
                label = cycle.Label,
                start = cycle.Start,
                end = cycle.End,
                isCurrent = cycle.IsCurrent,
                opening = cycle.Opening,
                moneyIn = cycle.MoneyIn,
                moneyOut = cycle.MoneyOut,
                closing = cycle.Closing,
                shortBuckets = cycle.ShortBuckets,
                stabilityTargetReached = cycle.StabilityTargetReached,
                lines = shown.Select(line => new
                {
                    date = line.Date,
                    label = line.Label,
                    bucket = line.Bucket ?? "Split across buckets",
                    amount = line.Amount,
                    kind = line.Kind == ForecastItemKinds.Bill ? "Bill" : "What-if",
                    billKey = line.SourceId
                }).ToList(),
                linesNotShown = lines.Count > shown.Count ? lines.Count - shown.Count : (int?)null
            };
        }).ToList();

        return AiToolResult.Of(new
        {
            today = response.Today,
            stabilityTarget = response.StabilityTarget,
            stabilityTargetReachedOn = result.StabilityTargetReachedOn,
            incomeKnown = result.IncomeKnown,
            everydaySpendingIncluded = result.EverydaySpendingIncluded,
            cycles,
            skippedBills = response.SkippedBills.Select(bill => new
            {
                date = bill.Date,
                label = bill.Label,
                bucket = bill.Bucket,
                amount = bill.Amount,
                billKey = bill.SourceId
            }).ToList(),
            assumptions = Assumptions(result)
        }, truncated: truncated);
    }

    private static CashFlowScenarioItem ParseItem(AiToolArgs item)
    {
        var date = item.OptionalDate("date") ?? throw new AiToolArgumentException("Each what-if needs a date.");
        var amount = item.OptionalDecimal("amount", -CashFlowForecastService.MaxAmount, CashFlowForecastService.MaxAmount)
            ?? throw new AiToolArgumentException("Each what-if needs an amount.");
        // Validate names the accepted buckets, so an unknown one passes through to be rejected there.
        var bucket = item.OptionalString("bucket", 64);
        return new CashFlowScenarioItem(date, amount, bucket, item.OptionalString("label", 200) ?? string.Empty);
    }

    // Income is split into one line per bucket; the model needs the movement, not its four shares.
    private static List<ForecastLine> Collapse(IReadOnlyList<ForecastLine> lines) =>
        lines
            .GroupBy(line => (line.Date, line.Label, line.Kind, line.SourceId))
            .Select(group =>
            {
                var buckets = group.Select(line => line.Bucket).Distinct().ToList();
                return new ForecastLine(
                    group.Key.Date, group.Key.Label, buckets.Count == 1 ? buckets[0] : null,
                    group.Sum(line => line.Amount), group.Key.Kind, group.Key.SourceId);
            })
            .ToList();

    private static string? PaymentId(string? billKey)
    {
        if (billKey is null) return null;
        var separator = billKey.LastIndexOf(':');
        return separator > 0 ? billKey[..separator] : null;
    }

    private static List<string> Assumptions(CashFlowForecastResult result)
    {
        var assumptions = new List<string>
        {
            "Starts from today's real bucket balances and takes every upcoming recurring bill and loan instalment; paused bills are left out.",
            "This cycle's salary is already in today's balances, so the current cycle receives no further income."
        };
        assumptions.Add(result.IncomeKnown
            ? "Income lands on each payday and is split by the allocation settings, stopping at the Stability target."
            : "No income was assumed for future cycles, so later balances show bills only.");
        if (!result.EverydaySpendingIncluded)
            assumptions.Add("Everyday spending beyond bills is not included.");
        return assumptions;
    }
}
