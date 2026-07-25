using FinancialAppApi.Controllers;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Tests;

public class InvestmentSnapshotValidationTests
{
    [Fact]
    public void ValidateTransactionSnapshot_AcceptsAConsistentInternalTransfer()
    {
        var outgoing = Transaction("TransferOut", accountId: Guid.NewGuid(), units: 5);
        var incoming = Transaction(
            "TransferIn",
            accountId: Guid.NewGuid(),
            units: 5,
            linkedTransferId: outgoing.Id,
            instrumentId: outgoing.InstrumentId);

        Assert.Null(InvestmentsController.ValidateTransactionSnapshot([outgoing, incoming]));
    }

    [Fact]
    public void ValidateTransactionSnapshot_RejectsAMismatchedInternalTransfer()
    {
        var outgoing = Transaction("TransferOut", accountId: Guid.NewGuid(), units: 5);
        var incoming = Transaction(
            "TransferIn",
            accountId: Guid.NewGuid(),
            units: 50,
            linkedTransferId: outgoing.Id,
            instrumentId: outgoing.InstrumentId);

        Assert.NotNull(InvestmentsController.ValidateTransactionSnapshot([outgoing, incoming]));
    }

    [Fact]
    public void ValidateTransactionSnapshot_RejectsALoneLinkedTransferLeg()
    {
        var incoming = Transaction(
            "TransferIn",
            accountId: Guid.NewGuid(),
            units: 5,
            linkedTransferId: Guid.NewGuid());

        Assert.NotNull(InvestmentsController.ValidateTransactionSnapshot([incoming]));
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
        Guid? linkedTransferId = null,
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
            1,
            linkedTransferId,
            DateTime.UtcNow);
}
