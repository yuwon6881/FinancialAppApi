using FinancialAppApi.Controllers;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FinancialAppApi.Tests;

public sealed class InvestmentAccountingEdgeCaseTests
{
    private readonly InvestmentAccountingService _accounting = new();
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly Guid _instrumentId = Guid.NewGuid();
    private readonly InvestmentInstrument _instrument = new()
    {
        Symbol = "EDGE",
        Name = "Edge case fund",
        Type = "ETF",
        Currency = "USD"
    };

    [Fact]
    public void DividendBeforeAnyBuyIsRejectedWithUserVisibleValidation()
    {
        var error = Assert.Throws<InvestmentValidationException>(() => _accounting.Calculate(
            [Transaction("Dividend", cashAmount: 10, date: new DateOnly(2026, 1, 1))], "USD"));

        Assert.Contains("before any units are held", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SplitLikeAdjustmentIsRejectedInsteadOfChangingThePosition()
    {
        var error = Assert.Throws<InvestmentValidationException>(() => _accounting.Calculate(
            [Transaction("Split", units: 2, date: new DateOnly(2026, 1, 1))], "USD"));

        Assert.Contains("Unsupported investment transaction type", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CashFlowWithoutAMatchingAccountIsRejectedByTheRoute()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = NewController(context);

        var result = await controller.CreateCashFlow(new CashFlowMutationDto(
            Guid.NewGuid(),
            "USD",
            "Deposit",
            100,
            new DateOnly(2026, 1, 1)));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("account", badRequest.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private InvestmentTransaction Transaction(
        string type,
        decimal units = 0,
        decimal? cashAmount = null,
        DateOnly date = default) => new()
    {
        AccountId = _accountId,
        InstrumentId = _instrumentId,
        Instrument = _instrument,
        Type = type,
        TradeDate = date == default ? new DateOnly(2026, 1, 1) : date,
        Units = units,
        UnitPrice = type == "Buy" ? 10 : null,
        CashAmount = cashAmount,
        CreatedAt = DateTime.UtcNow
    };

    private static InvestmentsController NewController(Database.AppDbContext context)
    {
        var provider = new EdgeProvider();
        var accounting = new InvestmentAccountingService();
        var options = Options.Create(new MarketDataOptions { ActiveProvider = provider.Descriptor.Id });
        var portfolio = new InvestmentPortfolioService(context, accounting, provider);
        var history = new InvestmentHistoryValidationService(context, accounting);
        var market = new InvestmentMarketDataService(
            context,
            provider,
            options,
            NullLogger<InvestmentMarketDataService>.Instance);
        return new InvestmentsController(
            context,
            portfolio,
            accounting,
            history,
            new InstrumentHistoryService(context, accounting, provider),
            new InvestmentQueryService(context),
            market,
            provider)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private sealed class EdgeProvider : IMarketDataProvider
    {
        public MarketDataProviderDescriptor Descriptor => new(
            "edge",
            "Edge provider",
            true,
            MarketDataCapabilities.RequiredForActivation,
            new MarketDataQuotaPolicy(10, 100, 100));

        public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InstrumentSearchResult>>([]);

        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(
            MarketInstrumentReference instrument,
            DateOnly startDate,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderPriceBar>>([]);

        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(
            string baseCurrency,
            string quoteCurrency,
            DateOnly startDate,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderFxBar>>([]);

        public MarketInstrumentReference? TryResolveLegacyReference(string? symbol, string? mic) => null;
    }
}
