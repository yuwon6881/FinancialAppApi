using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests;

public sealed class InvestmentAccountingServiceTests
{
    private readonly InvestmentAccountingService _service = new();
    private readonly Guid _account = Guid.NewGuid();
    private readonly Guid _instrumentId = Guid.NewGuid();
    private readonly InvestmentInstrument _instrument = new()
    {
        Symbol = "TEST",
        Name = "Test share",
        Type = "Stock",
        Currency = "USD"
    };

    [Fact]
    public void WeightedAverage_HandlesFeesTaxesPartialSellAndDividend()
    {
        var transactions = new[]
        {
            Tx("Buy", new DateOnly(2025, 1, 1), units: 10, price: 10, fees: 1),
            Tx("Buy", new DateOnly(2025, 2, 1), units: 10, price: 20, fees: 2, taxes: 1),
            Tx("Dividend", new DateOnly(2025, 2, 15), cash: 12, taxes: 2),
            Tx("Sell", new DateOnly(2025, 4, 1), units: 10, price: 25, fees: 3, taxes: 2),
        };

        var result = _service.Calculate(transactions, "USD");
        var position = Assert.Single(result.Positions);

        Assert.Equal(10m, position.Units);
        Assert.Equal(152m, position.CostBasisNative);
        Assert.Equal(93m, position.RealisedNative);
        Assert.Equal(10m, position.DividendsNative);
        Assert.Equal(59m, position.NetContributionsNative);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ForeignCurrencyWithoutTradeFx_KeepsNativeValuesAndMarksAppValuesIncomplete()
    {
        _instrument.Currency = "EUR";
        var result = _service.Calculate(
            [Tx("Buy", new DateOnly(2025, 1, 1), units: 2, price: 50)],
            "USD");

        var position = Assert.Single(result.Positions);
        Assert.Equal(100m, position.CostBasisNative);
        Assert.Null(position.CostBasisApp);
        Assert.Contains(result.Warnings, value => value.Contains("Historical FX", StringComparison.Ordinal));
    }

    [Fact]
    public void ForeignCurrencyWithHistoricalFxFallback_ValuesAppAmountsWithoutManualRate()
    {
        _instrument.Currency = "EUR";
        var result = _service.Calculate(
            [
                Tx("Buy", new DateOnly(2025, 1, 1), units: 2, price: 50),
                Tx("Dividend", new DateOnly(2025, 3, 1), cash: 10),
            ],
            "USD",
            // The reporting rate always comes from the market fallback (e.g. a stored
            // provider daily close).
            _ => 1.1m);

        var position = Assert.Single(result.Positions);
        Assert.Equal(100m, position.CostBasisNative);
        Assert.Equal(110m, position.CostBasisApp);
        Assert.Equal(10m, position.DividendsNative);
        Assert.Equal(11m, position.DividendsApp);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ForeignCurrencyTrade_UsesHistoricalFxFallbackRate()
    {
        _instrument.Currency = "EUR";
        var trade = Tx("Buy", new DateOnly(2025, 1, 1), units: 2, price: 50);

        var result = _service.Calculate([trade], "USD", _ => 1.25m);

        var position = Assert.Single(result.Positions);
        Assert.Equal(125m, position.CostBasisApp);
    }

    [Fact]
    public void OversellingIsRejectedAfterHistoricalEdit()
    {
        var transactions = new[]
        {
            Tx("Buy", new DateOnly(2025, 1, 1), units: 2, price: 10),
            Tx("Sell", new DateOnly(2025, 2, 1), units: 3, price: 12)
        };

        var error = Assert.Throws<InvestmentValidationException>(() => _service.Calculate(transactions, "USD"));
        Assert.Contains("more units", error.Message);
    }

    private InvestmentTransaction Tx(
        string type,
        DateOnly date,
        decimal units = 0,
        decimal? price = null,
        decimal? cash = null,
        decimal fees = 0,
        decimal taxes = 0) => new()
    {
        AccountId = _account,
        InstrumentId = _instrumentId,
        Instrument = _instrument,
        Type = type,
        TradeDate = date,
        Units = units,
        UnitPrice = price,
        CashAmount = cash,
        Fees = fees,
        Taxes = taxes,
        CreatedAt = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
    };
}
