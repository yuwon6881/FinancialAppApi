using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Investments;

public sealed partial class InvestmentPortfolioService
{
    public Task<InvestmentPortfolioDto> GetPortfolioAsync(string range, CancellationToken cancellationToken) =>
        BuildPortfolioAsync(range, includeChart: true, cancellationToken);

    public async Task<InvestmentAllocationOverviewDto> GetAllocationAsync(CancellationToken cancellationToken) =>
        (await BuildPortfolioAsync("1m", includeChart: false, cancellationToken)).Allocation;

    private IReadOnlyList<InvestmentChartPointDto> BuildChartIfRequested(
        bool includeChart,
        string range,
        IReadOnlyList<InvestmentTransaction> transactions,
        IReadOnlyList<InvestmentCashFlow> cashFlows,
        IReadOnlyList<InvestmentInstrument> instruments,
        IReadOnlyDictionary<Guid, MarketInstrumentReference> references,
        IReadOnlyList<MarketPriceBar> priceBars,
        IReadOnlyList<FxRateBar> fxBars,
        string appCurrency) =>
        includeChart
            ? BuildChart(range, transactions, cashFlows, instruments, references, priceBars, fxBars, appCurrency)
            : Array.Empty<InvestmentChartPointDto>();
}
