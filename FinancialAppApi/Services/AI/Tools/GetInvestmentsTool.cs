using System.Text.Json.Nodes;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Services.AI.Tools;

// Stored portfolio calculations only: no market provider is called from chat. A missing price or
// exchange rate stays null and is dated, never filled in.
public sealed class GetInvestmentsTool : IAiTool
{
    private static readonly string[] Ranges = ["1m", "3m", "6m", "1y", "3y", "5y", "all"];

    private readonly InvestmentPortfolioService _portfolio;

    public GetInvestmentsTool(InvestmentPortfolioService portfolio) => _portfolio = portfolio;

    public string Name => "get_investments";

    public string Description =>
        "The user's stored investment portfolio for a time range: totals (latest value, on paper, already banked, " +
        "dividends received, you paid, sent to broker), allocation, and the largest holdings with price and valuation " +
        "dates. Explains stored calculations only: never infer market causes, recommend a security, or suggest trades. " +
        "Missing prices stay unavailable.";

    public JsonObject ParametersSchema { get; } = AiToolSchema.Object(
    [
        ("range", AiToolSchema.Enum("Performance window. Default 1y.", Ranges)),
        ("instrumentId", AiToolSchema.String("Return only this holding's instrument id."))
    ]);

    // The whole answer is money; there is nothing honest to show with the figures removed.
    public AiToolSensitivity Sensitivity => AiToolSensitivity.HiddenWhenSensitive;

    public string ProgressLabel(AiToolArgs args) => "Checking your portfolio";

    public async Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken)
    {
        var range = args.OptionalEnum("range", Ranges) ?? "1y";
        var instrumentText = args.OptionalString("instrumentId", 64);
        Guid? instrumentId = null;
        if (instrumentText != null)
        {
            instrumentId = Guid.TryParse(instrumentText, out var parsed)
                ? parsed
                : throw new AiToolArgumentException("instrumentId must be an id returned by get_investments.");
        }

        var portfolio = await _portfolio.GetPortfolioAsync(range, cancellationToken);
        var holdings = portfolio.Holdings
            .Where(holding => instrumentId == null || holding.InstrumentId == instrumentId)
            .OrderByDescending(holding => holding.ValueApp.HasValue)
            .ThenByDescending(holding => holding.ValueApp)
            .ThenBy(holding => holding.Symbol, StringComparer.OrdinalIgnoreCase)
            .Take(instrumentId == null ? 10 : 1)
            .Select(holding => new
            {
                id = holding.InstrumentId,
                symbol = holding.Symbol,
                name = holding.Name,
                account = holding.AccountName,
                currency = holding.Currency,
                units = holding.Units,
                latestPrice = holding.LatestPriceNative,
                value = holding.ValueApp,
                onPaper = holding.UnrealisedProfitLossApp,
                onPaperPercent = holding.UnrealisedPercent,
                alreadyBanked = holding.RealisedProfitLossApp,
                dividendsReceived = holding.NetDividendsApp,
                valuationDate = holding.ValuationAsOf,
                priceDate = holding.PriceDate,
                exchangeRateDate = holding.FxDate,
                incomplete = holding.LatestPriceNative is null || holding.FxIncomplete || holding.ValueApp is null
            })
            .ToList();
        if (instrumentId != null && holdings.Count == 0)
            throw new AiToolArgumentException("No holding has that instrumentId. Call get_investments without it to list holdings.");

        context.Evidence.RecordAll(AiEvidenceLedger.Instrument, holdings.Select(holding => holding.id.ToString()));
        return AiToolResult.Of(new
        {
            range,
            coverage = new
            {
                source = "stored portfolio calculations",
                pricesUpdatedAt = portfolio.PricesUpdatedAt,
                marketDataConfigured = portfolio.MarketDataConfigured,
                warnings = portfolio.Warnings,
                incompleteHoldings = holdings.Count(holding => holding.incomplete)
            },
            summary = new
            {
                latestValue = portfolio.Summary.TotalValue,
                onPaper = portfolio.Summary.UnrealisedProfitLoss,
                alreadyBanked = portfolio.Summary.RealisedProfitLoss,
                dividendsReceived = portfolio.Summary.NetDividends,
                youPaid = portfolio.Summary.CostBasis,
                sentToBroker = portfolio.Summary.NetDeposits,
                cashValue = portfolio.Summary.CashValue
            },
            allocation = instrumentId == null ? portfolio.Allocation : null,
            holdings,
            totalHoldings = portfolio.Holdings.Count
        });
    }
}
