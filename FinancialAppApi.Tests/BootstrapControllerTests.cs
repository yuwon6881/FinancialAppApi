using System.Text.Json;
using FinancialAppApi.Controllers;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Documents;
using FinancialAppApi.Services.Investments;
using FinancialAppApi.Services.Loans;
using FinancialAppApi.Services.SavingsGoals;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public sealed class BootstrapControllerTests
{
    private static BootstrapController CreateController(AppDbContext context)
    {
        var occurrenceService = new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        var cycleBalanceService = new CycleBalanceService(context);
        var alertService = new RecurringPaymentAlertService(context, occurrenceService);
        var financialService = new FinancialService(context, cycleBalanceService, alertService, occurrenceService);
        var transactionQueryService = new TransactionQueryService(context);
        var recurringPaymentService = new RecurringPaymentService(context);
        var payEarlyService = new RecurringPaymentPayEarlyService(context, occurrenceService, cycleBalanceService);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var categoryService = new TransactionCategoryService(context, cache);
        var savingsGoalService = new SavingsGoalService(context, cycleBalanceService);
        var wishlistService = new WishlistService(context, cycleBalanceService, savingsGoalService);
        var loanService = new LoanService(context);
        var investmentPortfolioService = new InvestmentPortfolioService(
            context,
            new InvestmentAccountingService(),
            new LocalMarketDataProvider());
        var documentVaultService = new DocumentVaultService(
            context,
            new FakeDocumentVaultStore(),
            new FixedOptionsMonitor<DocumentVaultOptions>(new DocumentVaultOptions()),
            NullLogger<DocumentVaultService>.Instance);
        var documentRetentionService = new DocumentRetentionService(context);

        return new BootstrapController(
            financialService,
            transactionQueryService,
            recurringPaymentService,
            payEarlyService,
            categoryService,
            wishlistService,
            savingsGoalService,
            loanService,
            investmentPortfolioService,
            documentVaultService,
            documentRetentionService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private sealed class LocalMarketDataProvider : IMarketDataProvider
    {
        public MarketDataProviderDescriptor Descriptor => new(
            "bootstrap-test",
            "Bootstrap test provider",
            false,
            MarketDataCapabilities.RequiredForActivation,
            new MarketDataQuotaPolicy(10, 100, 100));

        public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(
            string query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstrumentSearchResult>>([]);

        public Task<IReadOnlyList<ProviderPriceBar>> GetDailySeriesAsync(
            MarketInstrumentReference instrument,
            DateOnly startDate,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderPriceBar>>([]);

        public Task<IReadOnlyList<ProviderFxBar>> GetFxSeriesAsync(
            string baseCurrency,
            string quoteCurrency,
            DateOnly startDate,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderFxBar>>([]);

        public MarketInstrumentReference? TryResolveLegacyReference(string? symbol, string? mic) => null;
    }

    private static JsonElement ToJsonElement(object value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task GetBootstrap_WithInvalidPeriod_ReturnsBadRequest()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = CreateController(context);

        var result = await controller.GetBootstrap(queryMonth: "InvalidMonth", queryYear: 2026);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetBootstrap_ReturnsCompletePayload_IncludingAccountsAndGoals()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = CreateController(context);

        context.FinancialSettings.Add(new FinancialSetting
        {
            UserId = TestHelpers.DefaultUserId,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            CycleDay = 1,
            EssentialsAlloc = 0.50m,
            GrowthAlloc = 0.30m,
            StabilityAlloc = 0.10m,
            RewardsAlloc = 0.10m,
        });

        context.LedgerAccounts.Add(new LedgerAccount
        {
            Id = "acct-essentials",
            Name = "Main Account",
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank,
            UserId = TestHelpers.DefaultUserId,
        });

        context.TransactionCategories.Add(new TransactionCategory
        {
            Id = "cat-food",
            Name = "Food & Groceries",
            UserId = TestHelpers.DefaultUserId,
        });

        context.Transactions.Add(new Transaction
        {
            Id = "tx-1",
            UserId = TestHelpers.DefaultUserId,
            Date = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
            PostedAt = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
            Description = "Groceries",
            Category = "Food & Groceries",
            LedgerCategory = "Essentials",
            Amount = 100m,
            AccountId = "acct-essentials",
        });

        await context.SaveChangesAsync();

        var actionResult = await controller.GetBootstrap(queryMonth: "Jul", queryYear: 2026, includeLoans: true);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var root = ToJsonElement(okResult.Value!);

        Assert.Equal("Jul", root.GetProperty("month").GetString());
        Assert.Equal(2026, root.GetProperty("year").GetInt32());
        Assert.True(root.TryGetProperty("dashboard", out _));
        Assert.True(root.TryGetProperty("insights", out _));
        Assert.True(root.TryGetProperty("transactions", out var txs));
        Assert.Single(txs.EnumerateArray());
        Assert.True(root.TryGetProperty("accounts", out var accounts));
        Assert.Single(accounts.EnumerateArray());
        Assert.True(root.TryGetProperty("loans", out var loans));
        Assert.Equal(JsonValueKind.Array, loans.ValueKind);
    }

    [Fact]
    public async Task GetBootstrap_WithIncludeLoansFalse_OmitsLoans()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = CreateController(context);

        context.FinancialSettings.Add(new FinancialSetting
        {
            UserId = TestHelpers.DefaultUserId,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            CycleDay = 1,
        });
        await context.SaveChangesAsync();

        var actionResult = await controller.GetBootstrap(queryMonth: "Jul", queryYear: 2026, includeLoans: false);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var root = ToJsonElement(okResult.Value!);

        Assert.True(root.TryGetProperty("loans", out var loans));
        Assert.Equal(JsonValueKind.Null, loans.ValueKind);
    }
}
