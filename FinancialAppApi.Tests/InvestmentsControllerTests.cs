using FinancialAppApi.Controllers;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Investments;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FinancialAppApi.Tests;

public sealed class InvestmentsControllerTests
{
    [Fact]
    public void DeclaresEveryInvestmentRoute()
    {
        var routes = typeof(InvestmentsController).GetMethods()
            .SelectMany(method => method.GetCustomAttributes(inherit: true)
                .OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>()
                .Select(attribute => $"{attribute.HttpMethods.Single()} {attribute.Template}"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(22, routes.Count);
        Assert.Contains("GET portfolio", routes);
        Assert.Contains("GET accounts", routes);
        Assert.Contains("GET transactions", routes);
        Assert.Contains("GET cash-flows", routes);
        Assert.Contains("GET instruments/search", routes);
        Assert.Contains("POST market-data/refresh", routes);
        Assert.Contains("POST transactions/restore", routes);
        Assert.Contains("POST cash-flows/restore", routes);
    }

    [Fact]
    public async Task ReadersReturnPagedContractsAndRejectInvalidRanges()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = NewController(context);

        var transactions = await controller.GetTransactions(
            null, null, null, null, null, page: 2, pageSize: 25);
        var transactionPage = Assert.IsType<OkObjectResult>(transactions.Result).Value;
        var transactionResult = Assert.IsType<PagedResult<InvestmentTransactionDto>>(transactionPage);
        Assert.Equal(2, transactionResult.Page);
        Assert.Equal(25, transactionResult.PageSize);
        Assert.Empty(transactionResult.Items);

        var cashFlows = await controller.GetCashFlows(null, null, null, null, page: 1, pageSize: 10);
        var cashFlowResult = Assert.IsType<PagedResult<InvestmentCashFlowDto>>(
            Assert.IsType<OkObjectResult>(cashFlows.Result).Value);
        Assert.Equal(1, cashFlowResult.Page);
        Assert.Equal(10, cashFlowResult.PageSize);

        Assert.IsType<BadRequestObjectResult>(
            (await controller.GetPortfolio("quarter")).Result);
        Assert.IsType<BadRequestObjectResult>(
            (await controller.GetTransactions(null, null, null, null, null, 0, 10)).Result);
        Assert.IsType<BadRequestObjectResult>(
            (await controller.GetCashFlows(null, null, null, null, 1, 20)).Result);
        Assert.IsType<BadRequestObjectResult>(
            (await controller.GetInstrumentHistory(Guid.NewGuid(), "quarter")).Result);
    }

    [Fact]
    public async Task AccountRoutesCreateUpdateCloseAndDeleteOnlyCurrentUsersRows()
    {
        await using var context = TestHelpers.NewInMemoryContext("alice");
        var bobAccount = new InvestmentAccount { UserId = "bob", Name = "Bob broker", BaseCurrency = "USD" };
        context.SetCurrentUser("bob");
        context.InvestmentAccounts.Add(bobAccount);
        await context.SaveChangesAsync();
        context.SetCurrentUser("alice");
        var controller = NewController(context);

        var created = await controller.CreateAccount(new AccountMutationDto("Alice broker", "usd"));
        var createdResult = Assert.IsType<CreatedResult>(created.Result);
        var account = Assert.IsType<InvestmentAccount>(createdResult.Value);
        Assert.Equal("USD", account.BaseCurrency);

        Assert.IsType<NotFoundResult>(await controller.UpdateAccount(
            bobAccount.Id, new AccountMutationDto("Changed", "USD")));
        Assert.IsType<NoContentResult>(await controller.UpdateAccount(
            account.Id, new AccountMutationDto("Alice updated", "USD")));
        Assert.IsType<NoContentResult>(await controller.UpdateAccount(
            account.Id, new AccountMutationDto("Alice closed", "USD", IsArchived: true)));
        Assert.IsType<OkObjectResult>((await controller.DeleteAccount(account.Id)).Result);

        var accounts = await controller.GetAccounts();
        var visible = Assert.IsType<List<InvestmentAccount>>(
            Assert.IsType<OkObjectResult>(accounts.Result).Value);
        Assert.DoesNotContain(visible, value => value.Id == bobAccount.Id);
    }

    [Fact]
    public async Task InstrumentAndSearchRoutesExposeValidationAndTenancy()
    {
        await using var context = TestHelpers.NewInMemoryContext("alice");
        var bobInstrument = new InvestmentInstrument
        {
            UserId = "bob", Symbol = "BOB", Name = "Bob fund", Type = "ETF", Currency = "USD"
        };
        context.SetCurrentUser("bob");
        context.InvestmentInstruments.Add(bobInstrument);
        await context.SaveChangesAsync();
        context.SetCurrentUser("alice");
        var controller = NewController(context);

        Assert.IsType<BadRequestObjectResult>((await controller.CreateInstrument(
            new InstrumentMutationDto("", "", "Invalid", "", null, null, null, null, null, false))).Result);

        var created = await controller.CreateInstrument(Instrument("ALC", "Alice fund"));
        var createdResult = Assert.IsType<CreatedResult>(created.Result);
        var instrument = Assert.IsType<InvestmentInstrument>(createdResult.Value);
        Assert.Equal("USD", instrument.Currency);
        Assert.IsType<NotFoundResult>(await controller.UpdateInstrument(
            bobInstrument.Id, Instrument("BOB2", "Changed")));
        Assert.IsType<NoContentResult>(await controller.UpdateInstrument(
            instrument.Id, Instrument("ALC2", "Alice fund updated")));
        Assert.IsType<OkObjectResult>((await controller.DeleteInstrument(instrument.Id)).Result);

        var search = await controller.SearchInstruments("ALC");
        var searchResult = Assert.IsType<InvestmentSearchResponse>(
            Assert.IsType<OkObjectResult>(search.Result).Value);
        Assert.Empty(searchResult.Results);
        Assert.IsType<NotFoundResult>((await controller.GetInstrumentHistory(bobInstrument.Id)).Result);
    }

    [Fact]
    public async Task MarketRefreshAndCurrencyRoutesHaveSafeManualFallbacks()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = NewController(context);

        var refresh = await controller.RefreshMarketData();
        var refreshResult = Assert.IsType<MarketRefreshResponse>(
            Assert.IsType<OkObjectResult>(refresh.Result).Value);
        Assert.True(refreshResult.Complete);
        Assert.Equal("ConfigurationRequired", refreshResult.Status);

        var currencies = controller.GetCurrencies();
        var catalog = Assert.IsAssignableFrom<IReadOnlyList<CurrencyCatalogItem>>(
            Assert.IsType<OkObjectResult>(currencies.Result).Value);
        Assert.Contains(catalog, value => value.Code == "USD");
    }

    [Fact]
    public async Task TransactionMutationsCanonicalizeTypesAndProtectCashHistoryOnDeleteAndRestore()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var account = new InvestmentAccount { Name = "Broker", BaseCurrency = "USD" };
        var instrument = new InvestmentInstrument
        {
            Symbol = "FUND", Name = "Fund", Type = "ETF", Currency = "USD", IsCustom = true
        };
        context.InvestmentAccounts.Add(account);
        context.InvestmentInstruments.Add(instrument);
        await context.SaveChangesAsync();
        var controller = NewController(context);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        Assert.IsType<CreatedResult>((await controller.CreateCashFlow(
            new CashFlowMutationDto(account.Id, "USD", "Deposit", 200, today))).Result);
        var buy = Assert.IsType<InvestmentTransactionDto>(
            Assert.IsType<CreatedResult>((await controller.CreateTransaction(
                new InvestmentTransactionMutationDto(
                    account.Id, instrument.Id, "buy", today, 1, 100, 100, 0, 0))).Result).Value);
        Assert.Equal("Buy", buy.Type);

        var sell = Assert.IsType<InvestmentTransactionDto>(
            Assert.IsType<CreatedResult>((await controller.CreateTransaction(
                new InvestmentTransactionMutationDto(
                    account.Id, instrument.Id, "Sell", today, 0.5m, 100, 50, 0, 0))).Result).Value);
        Assert.IsType<CreatedResult>((await controller.CreateCashFlow(
            new CashFlowMutationDto(account.Id, "USD", "Withdrawal", 150, today))).Result);

        Assert.IsType<ConflictObjectResult>((await controller.DeleteTransaction(sell.Id)).Result);

        await using var restoreContext = TestHelpers.NewInMemoryContext();
        var restoreAccount = new InvestmentAccount { Name = "Restore broker", BaseCurrency = "USD" };
        var restoreInstrument = new InvestmentInstrument
        {
            Symbol = "UNDO", Name = "Undo fund", Type = "ETF", Currency = "USD", IsCustom = true
        };
        restoreContext.InvestmentAccounts.Add(restoreAccount);
        restoreContext.InvestmentInstruments.Add(restoreInstrument);
        await restoreContext.SaveChangesAsync();
        var restoreController = NewController(restoreContext);
        Assert.IsType<CreatedResult>((await restoreController.CreateCashFlow(
            new CashFlowMutationDto(restoreAccount.Id, "USD", "Deposit", 100, today))).Result);
        var removableBuy = Assert.IsType<InvestmentTransactionDto>(
            Assert.IsType<CreatedResult>((await restoreController.CreateTransaction(
                new InvestmentTransactionMutationDto(
                    restoreAccount.Id, restoreInstrument.Id, "Buy", today, 1, 50, 50, 0, 0))).Result).Value);
        var snapshot = Assert.IsType<DeletedTransactionsSnapshot>(
            Assert.IsType<OkObjectResult>((await restoreController.DeleteTransaction(removableBuy.Id)).Result).Value);
        Assert.IsType<CreatedResult>((await restoreController.CreateCashFlow(
            new CashFlowMutationDto(restoreAccount.Id, "USD", "Withdrawal", 100, today))).Result);

        Assert.IsType<ConflictObjectResult>(await restoreController.RestoreTransactions(snapshot));
    }

    private static InstrumentMutationDto Instrument(string symbol, string name)
        => new(symbol, name, "ETF", "usd", null, null, null, null, null, true);

    private static InvestmentsController NewController(Database.AppDbContext context)
    {
        var provider = new TestProvider();
        var accounting = new InvestmentAccountingService();
        var options = Options.Create(new MarketDataOptions { ActiveProvider = provider.Descriptor.Id });
        return new InvestmentsController(
            context,
            new InvestmentPortfolioService(context, accounting, provider),
            accounting,
            new InvestmentHistoryValidationService(context, accounting),
            new InstrumentHistoryService(context, accounting, provider),
            new InvestmentQueryService(context),
            new InvestmentMarketDataService(
                context, provider, options,
                NullLogger<InvestmentMarketDataService>.Instance),
            provider)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private sealed class TestProvider : IMarketDataProvider
    {
        public MarketDataProviderDescriptor Descriptor => new(
            "controller-test", "Controller test provider", false,
            MarketDataCapabilities.RequiredForActivation,
            new MarketDataQuotaPolicy(10, 100, 100));

        public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InstrumentSearchResult>>([]);

        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(
            MarketInstrumentReference instrument, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderPriceBar>>([]);

        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(
            string baseCurrency, string quoteCurrency, DateOnly startDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderFxBar>>([]);

        public MarketInstrumentReference? TryResolveLegacyReference(string? symbol, string? mic) => null;
    }
}
