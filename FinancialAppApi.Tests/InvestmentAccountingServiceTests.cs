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
    public void WeightedAverage_HandlesFeesTaxesPartialSellDividendAndSplit()
    {
        var transactions = new[]
        {
            Tx("OpeningPosition", new DateOnly(2025, 1, 1), units: 10, price: 10, fees: 1),
            Tx("Buy", new DateOnly(2025, 2, 1), units: 10, price: 20, fees: 2, taxes: 1),
            Tx("Dividend", new DateOnly(2025, 2, 15), cash: 12, taxes: 2),
            Tx("Split", new DateOnly(2025, 3, 1), units: 2),
            Tx("Sell", new DateOnly(2025, 4, 1), units: 10, price: 25, fees: 3, taxes: 2),
        };

        var result = _service.Calculate(transactions, "USD");
        var position = Assert.Single(result.Positions);

        Assert.Equal(30m, position.Units);
        Assert.Equal(228m, position.CostBasisNative);
        Assert.Equal(169m, position.RealisedNative);
        Assert.Equal(10m, position.DividendsNative);
        Assert.Equal(59m, position.NetContributionsNative);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void InternalTransfer_PreservesProportionalCostBasisAndCreatesNoGain()
    {
        var destination = Guid.NewGuid();
        var transferOut = Tx("TransferOut", new DateOnly(2025, 2, 1), units: 4);
        var transferIn = Tx("TransferIn", new DateOnly(2025, 2, 1), units: 4);
        transferIn.AccountId = destination;
        transferIn.LinkedTransferId = transferOut.Id;
        // Deliberately tie the timestamps and make the incoming GUID sort first. Accounting must
        // still honor the explicit transfer dependency rather than random GUID order.
        transferOut.Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        transferIn.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        transferIn.LinkedTransferId = transferOut.Id;
        var transactions = new[]
        {
            Tx("Buy", new DateOnly(2025, 1, 1), units: 10, price: 15),
            transferOut,
            transferIn
        };

        var result = _service.Calculate(transactions, "USD");
        var source = Assert.Single(result.Positions, value => value.AccountId == _account);
        var target = Assert.Single(result.Positions, value => value.AccountId == destination);

        Assert.Equal(6m, source.Units);
        Assert.Equal(90m, source.CostBasisNative);
        Assert.Equal(4m, target.Units);
        Assert.Equal(60m, target.CostBasisNative);
        Assert.All(result.Positions, value => Assert.Equal(0m, value.RealisedNative));
    }

    [Fact]
    public void InternalTransfer_CannotConsumeTheSameBasisTwice()
    {
        var transferOut = Tx("TransferOut", new DateOnly(2025, 2, 1), units: 4);
        var firstIn = Tx("TransferIn", new DateOnly(2025, 2, 1), units: 4);
        firstIn.AccountId = Guid.NewGuid();
        firstIn.LinkedTransferId = transferOut.Id;
        firstIn.CreatedAt = transferOut.CreatedAt.AddSeconds(1);
        var duplicateIn = Tx("TransferIn", new DateOnly(2025, 2, 1), units: 4);
        duplicateIn.AccountId = Guid.NewGuid();
        duplicateIn.LinkedTransferId = transferOut.Id;
        duplicateIn.CreatedAt = transferOut.CreatedAt.AddSeconds(2);

        var error = Assert.Throws<InvestmentValidationException>(() => _service.Calculate(
            [
                Tx("Buy", new DateOnly(2025, 1, 1), units: 10, price: 15),
                transferOut,
                firstIn,
                duplicateIn
            ],
            "USD"));

        Assert.Contains("one unused transfer-out", error.Message);
    }

    [Fact]
    public void InternalTransfer_RequiresMatchingUnits()
    {
        var transferOut = Tx("TransferOut", new DateOnly(2025, 2, 1), units: 4);
        var transferIn = Tx("TransferIn", new DateOnly(2025, 2, 1), units: 5);
        transferIn.AccountId = Guid.NewGuid();
        transferIn.LinkedTransferId = transferOut.Id;
        transferIn.CreatedAt = transferOut.CreatedAt.AddSeconds(1);

        var error = Assert.Throws<InvestmentValidationException>(() => _service.Calculate(
            [
                Tx("Buy", new DateOnly(2025, 1, 1), units: 10, price: 15),
                transferOut,
                transferIn
            ],
            "USD"));

        Assert.Contains("must match", error.Message);
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
            // No manual TradeFxRate on either transaction: the reporting rate comes
            // from the market fallback (e.g. a stored provider daily close).
            _ => 1.1m);

        var position = Assert.Single(result.Positions);
        Assert.Equal(100m, position.CostBasisNative);
        Assert.Equal(110m, position.CostBasisApp);
        Assert.Equal(10m, position.DividendsNative);
        Assert.Equal(11m, position.DividendsApp);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ManualTradeFxOverridesHistoricalFallback()
    {
        _instrument.Currency = "EUR";
        var trade = Tx("Buy", new DateOnly(2025, 1, 1), units: 2, price: 50);
        trade.TradeFxRate = 1.25m;

        var result = _service.Calculate([trade], "USD", _ => 1.1m);

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
