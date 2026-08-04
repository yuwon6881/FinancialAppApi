namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private async Task<object?> BuildInvestmentContextAsync(
        AiIntentPlan intentPlan,
        bool sensitiveMode,
        CancellationToken cancellationToken)
    {
        if (!intentPlan.QueryPlan.NeedsInvestments) return null;
        if (sensitiveMode)
        {
            return new
            {
                available = false,
                reason = "Investment amounts and holdings are hidden while sensitive mode is active.",
                historyRedacted = true
            };
        }
        if (_investmentPortfolioService == null)
        {
            return new
            {
                available = false,
                reason = "Investment portfolio data is not configured for this account."
            };
        }

        var range = NormalizeInvestmentRange(intentPlan.ConversationState.LastInvestmentRange);
        var portfolio = await _investmentPortfolioService.GetPortfolioAsync(range, cancellationToken);
        var holdings = portfolio.Holdings
            .OrderByDescending(holding => holding.ValueApp.HasValue)
            .ThenByDescending(holding => holding.ValueApp)
            .ThenBy(holding => holding.Symbol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(holding => holding.InstrumentId)
            .Take(10)
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

        return new
        {
            available = true,
            range,
            coverage = new
            {
                source = "stored portfolio calculations",
                marketProviderCalls = 0,
                pricesUpdatedAt = portfolio.PricesUpdatedAt,
                marketDataConfigured = portfolio.MarketDataConfigured,
                warnings = portfolio.Warnings,
                incompleteHoldings = holdings.Count(holding => holding.incomplete)
            },
            summary = new
            {
                onPaper = portfolio.Summary.UnrealisedProfitLoss,
                alreadyBanked = portfolio.Summary.RealisedProfitLoss,
                dividendsReceived = portfolio.Summary.NetDividends,
                youPaid = portfolio.Summary.CostBasis,
                sentToBroker = portfolio.Summary.NetDeposits,
                latestValue = portfolio.Summary.TotalValue,
                cashValue = portfolio.Summary.CashValue,
                valuationDate = portfolio.Holdings
                    .Where(holding => holding.ValuationAsOf.HasValue)
                    .Select(holding => holding.ValuationAsOf)
                    .Max()
            },
            allocation = portfolio.Allocation,
            holdings,
            limitations = new[]
            {
                "Explain stored calculations only; do not infer market or news causes.",
                "Missing prices or exchange rates stay unavailable and must be dated in the answer.",
                "Do not recommend securities or emit buy, sell, or rebalance actions."
            }
        };
    }

    private static string NormalizeInvestmentRange(string? range) => range switch
    {
        "1m" or "3m" or "6m" or "1y" or "3y" or "5y" or "all" => range,
        _ => "1y"
    };
}