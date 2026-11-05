using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Investments;

public sealed partial class InvestmentPortfolioService
{
    public Task<InvestmentPortfolioDto> GetPortfolioAsync(string range, CancellationToken cancellationToken) =>
        BuildPortfolioAsync(range, includeChart: true, cancellationToken);

    public async Task<InvestmentAllocationOverviewDto> GetAllocationAsync(CancellationToken cancellationToken) =>
        (await BuildPortfolioAsync("1m", includeChart: false, cancellationToken)).Allocation;

    private async Task<IReadOnlyList<InvestmentChartPointDto>> BuildChartIfRequestedAsync(
        bool includeChart,
        string range,
        IReadOnlyList<InvestmentTransaction> transactions,
        IReadOnlyList<InvestmentCashFlow> cashFlows,
        IReadOnlyList<InvestmentInstrument> instruments,
        IReadOnlyDictionary<Guid, MarketInstrumentReference> references,
        IReadOnlyList<MarketPriceBar> priceBars,
        IReadOnlyList<FxRateBar> fxBars,
        string appCurrency,
        CancellationToken cancellationToken)
    {
        if (!includeChart) return [];
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var first = transactions.Select(value => (DateOnly?)value.TradeDate)
            .Concat(cashFlows.Select(value => (DateOnly?)value.Date))
            .Min();
        // Only a long range reads the month-end cache; a short one is cheap to value day by day.
        var settled = periodSnapshots is not null && first is { } start &&
                      InvestmentChartRange.UsesMonthlyHistory(InvestmentChartRange.StartFor(range, today) ?? start, today)
            ? (await periodSnapshots.EnsureAsync(cancellationToken))
                .Where(value => value.Segment == InvestmentSnapshotSegments.Total)
                .OrderBy(value => value.PeriodEnd)
                .ToList()
            : [];
        return BuildChart(range, transactions, cashFlows, instruments, references, priceBars, fxBars, appCurrency, settled);
    }
}
