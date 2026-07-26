using FinancialAppApi.Controllers;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests;

public class InvestmentSnapshotValidationTests
{
    [Fact]
    public void ValidateTransactionSnapshot_AcceptsASupportedActivity()
    {
        Assert.Null(InvestmentsController.ValidateTransactionSnapshot(
            [Transaction("Buy", accountId: Guid.NewGuid(), units: 5)]));
    }

    [Fact]
    public void ValidateTransactionSnapshot_RejectsARemovedActivityType()
    {
        Assert.NotNull(InvestmentsController.ValidateTransactionSnapshot(
            [Transaction("Unsupported", accountId: Guid.NewGuid(), units: 5)]));
    }

    [Fact]
    public void HasHistoricalMarketIdentityChange_DetectsCurrencyProviderAndCustomChanges()
    {
        var existing = new InvestmentInstrument
        {
            Currency = "USD",
            IsCustom = false,
            ProviderSymbol = "AAPL",
            ProviderMic = "XNAS"
        };
        var unchanged = Instrument("USD", false, "AAPL", "XNAS");

        Assert.False(InvestmentsController.HasHistoricalMarketIdentityChange(existing, unchanged));
        Assert.True(InvestmentsController.HasHistoricalMarketIdentityChange(
            existing, Instrument("MYR", false, "AAPL", "XNAS")));
        Assert.True(InvestmentsController.HasHistoricalMarketIdentityChange(
            existing, Instrument("USD", false, "AAPL.US", "XNAS")));
        Assert.True(InvestmentsController.HasHistoricalMarketIdentityChange(
            existing, Instrument("USD", true, null, null)));
    }

    private static InstrumentMutationDto Instrument(
        string currency,
        bool isCustom,
        string? providerSymbol,
        string? providerMic)
        => new(
            "AAPL",
            "Apple",
            "Stock",
            currency,
            null,
            null,
            null,
            providerSymbol,
            providerMic,
            isCustom);

    private static InvestmentTransactionDto Transaction(
        string type,
        Guid accountId,
        decimal units,
        Guid? instrumentId = null)
        => new(
            Guid.NewGuid(),
            accountId,
            instrumentId ?? Guid.NewGuid(),
            type,
            new DateOnly(2026, 7, 1),
            units,
            null,
            null,
            0,
            0,
            DateTime.UtcNow);
}
