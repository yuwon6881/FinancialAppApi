using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Loans;
using FinancialAppApi.Contracts;
using FinancialAppApi.Services.Documents;
using FinancialAppApi.Services.Investments;

namespace FinancialAppApi.Controllers;

/// <summary>
/// One request that returns everything the app needs to render its first screen.
/// </summary>
/// <remarks>
/// A cold launch used to issue eight GETs (dashboard, transactions, recurring payments,
/// categories, wishlist, autocomplete, wallet balance, insights) — and two of them could not
/// even start until the dashboard response arrived, because the client learned the active
/// month/year from it. On a phone each request is its own round trip plus token validation,
/// so that dependency chain was the largest fixed cost of starting the app.
///
/// This endpoint resolves the period once up front and composes the same payloads from the
/// same services and mappers the individual endpoints use, so there is one shape of truth.
/// Those endpoints all remain, and are still the right thing to call for a targeted refresh
/// of a single slice.
///
/// The reads are sequential on purpose: they share one request-scoped AppDbContext, which is
/// not thread-safe. The win here is collapsing eight HTTP round trips into one, not
/// parallelising the queries behind it.
/// </remarks>
[ApiController]
[Route("api/bootstrap")]
[AuthorizeToken]
[NoFinancialRefresh]
public class BootstrapController : ControllerBase
{
    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly FinancialService _financialService;
    private readonly TransactionQueryService _transactionQueryService;
    private readonly RecurringPaymentService _recurringPaymentService;
    private readonly RecurringPaymentPayEarlyService _payEarlyService;
    private readonly TransactionCategoryService _categoryService;
    private readonly WishlistService _wishlistService;
    private readonly Services.SavingsGoals.SavingsGoalService _savingsGoalService;
    private readonly LoanService _loanService;
    private readonly InvestmentPortfolioService _investmentPortfolioService;
    private readonly DocumentVaultService _documentVaultService;
    private readonly DocumentRetentionService _documentRetentionService;

    public BootstrapController(
        FinancialService financialService,
        TransactionQueryService transactionQueryService,
        RecurringPaymentService recurringPaymentService,
        RecurringPaymentPayEarlyService payEarlyService,
        TransactionCategoryService categoryService,
        WishlistService wishlistService,
        Services.SavingsGoals.SavingsGoalService savingsGoalService,
        LoanService loanService,
        InvestmentPortfolioService investmentPortfolioService,
        DocumentVaultService documentVaultService,
        DocumentRetentionService documentRetentionService)
    {
        _savingsGoalService = savingsGoalService;
        _loanService = loanService;
        _financialService = financialService;
        _transactionQueryService = transactionQueryService;
        _recurringPaymentService = recurringPaymentService;
        _payEarlyService = payEarlyService;
        _categoryService = categoryService;
        _wishlistService = wishlistService;
        _investmentPortfolioService = investmentPortfolioService;
        _documentVaultService = documentVaultService;
        _documentRetentionService = documentRetentionService;
    }

    // GET: api/bootstrap?month=Jul&year=2026
    [HttpGet]
    public async Task<ActionResult<object>> GetBootstrap(
        [FromQuery(Name = "month")] string? queryMonth = null,
        [FromQuery(Name = "year")] int? queryYear = null,
        [FromQuery(Name = "persistSelection")] bool persistSelection = true,
        [FromQuery(Name = "includeLoans")] bool includeLoans = true)
    {
        if (!IsValidPeriod(queryMonth, queryYear))
            return BadRequest(new { message = "Month must be a valid three-letter abbreviation and include a year." });

        var cancellationToken = HttpContext.RequestAborted;

        // Resolve (and optionally persist) the active cycle once. Everything below is then
        // built for one explicit period, which is what removes the client-side waterfall.
        var snapshot = await _financialService.CreateBootstrapSnapshotAsync(
            queryMonth,
            queryYear,
            persistSelection,
            cancellationToken);
        var month = snapshot.Cycle.ActiveMonth;
        var year = snapshot.Cycle.ActiveYear;

        // persistSelection: false — the line above is the writer of record for this request,
        // so the dashboard call must not redundantly re-save the same values.
        var dashboard = await _financialService.GetDashboardDataAsync(
            snapshot,
            summaryOnly: false,
            cancellationToken);
        var insights = await _financialService.GetDashboardInsightsAsync(
            snapshot.Cycle,
            cancellationToken);

        var transactions = TransactionQueryService.ProjectCycleTransactions(
            snapshot.CycleRelevantTransactions,
            snapshot.Cycle.ActiveYear,
            snapshot.Cycle.ActiveMonthIndex,
            snapshot.Cycle.CycleDay);
        if (transactions.Items.Any(TransactionQueryService.CanCarryStabilityReloadStatus))
        {
            var stabilityReloadStatuses = await _transactionQueryService.GetStabilityReloadStatusMapAsync(
                cancellationToken,
                transactions.Items.Select(item => item.Id).ToList());
            transactions = TransactionQueryService.ProjectCycleTransactions(
                snapshot.CycleRelevantTransactions,
                snapshot.Cycle.ActiveYear,
                snapshot.Cycle.ActiveMonthIndex,
                snapshot.Cycle.CycleDay,
                stabilityReloadStatuses);
        }

        var recurringPayments = await RecurringPaymentsController.BuildRecurringPaymentDtosAsync(
            _recurringPaymentService, _payEarlyService, cancellationToken);

        var categories = await _categoryService.GetCategoriesAsync(cancellationToken);
        var wishlist = await _wishlistService.GetWishlistAsync(cancellationToken);
        var savingsGoals = await _savingsGoalService.GetGoalsAsync(cancellationToken);
        List<LoanDto>? loanDtos = null;
        if (includeLoans)
        {
            var loans = await _loanService.GetLoansAsync(cancellationToken);
            loanDtos = loans.Select(LoansController.MapToDto).ToList();
        }
        var autocomplete = await _transactionQueryService.GetAutocompleteSuggestionsAsync(cancellationToken);
        var walletBalance = await _financialService.GetWalletBalanceAsync(snapshot, cancellationToken);
        var accounts = snapshot.LedgerAccounts;
        var accountBalances = snapshot.LedgerAccountBalances.Current;

        return Ok(new
        {
            month,
            year,
            dashboard,
            insights,
            transactions = transactions.Items.Select(TransactionsController.MapToDto).ToList(),
            recurringPayments,
            categories = categories.Select(TransactionCategoriesController.ToResponse).ToList(),
            wishlist = wishlist.Select(WishlistController.MapToDto).ToList(),
            savingsGoals = savingsGoals.Select(SavingsGoalsController.MapToDto).ToList(),
            loans = loanDtos,
            autocomplete,
            walletBalance,
            accounts = accounts.Select(account => LedgerAccountsController.MapToDto(account, accountBalances)).ToList(),
        });
    }

    // GET: api/bootstrap/refresh?month=Jul&year=2026&slices=core,recurring
    //
    // This is intentionally a separate route and never persists the selected period. It is used
    // after a successful mutation, when the client already has an optimistic projection and only
    // needs the read models affected by that mutation.
    [HttpGet("refresh")]
    public async Task<ActionResult<BootstrapRefreshResponse>> Refresh(
        [FromQuery(Name = "month")] string? queryMonth = null,
        [FromQuery(Name = "year")] int? queryYear = null,
        [FromQuery(Name = "slices")] string? slices = null)
    {
        if (!IsValidPeriod(queryMonth, queryYear))
            return BadRequest(new { message = "Month must be a valid three-letter abbreviation and include a year." });
        if (!RefreshSliceNames.TryParseRequest(slices, out var requestedSlices, out var sliceError))
            return BadRequest(new { message = sliceError });

        var requested = requestedSlices.ToHashSet(StringComparer.Ordinal);
        var cancellationToken = HttpContext.RequestAborted;

        // Resolve the period without loading the full financial snapshot unless core was asked
        // for. A categories/goals/loans-only refresh should not pay for transaction replay,
        // account balances, and recurring occurrence materialization. Core remains one shared
        // snapshot that all of its overlapping values reuse, and no path persists selection.
        FinancialService.FinancialBootstrapSnapshot? snapshot = null;
        string month;
        int year;
        if (requested.Contains(RefreshSliceNames.Core))
        {
            snapshot = await _financialService.CreateBootstrapSnapshotAsync(
                queryMonth,
                queryYear,
                persistSelection: false,
                cancellationToken);
            month = snapshot.Cycle.ActiveMonth;
            year = snapshot.Cycle.ActiveYear;
        }
        else
        {
            (month, year) = await _financialService.ResolveActivePeriodAsync(
                queryMonth,
                queryYear,
                persist: false,
                cancellationToken);
        }
        var response = new BootstrapRefreshResponse
        {
            Month = month,
            Year = year,
        };

        // Core is deliberately composed from the same snapshot and mappers as full bootstrap so
        // overlapping values cannot drift between the two contracts.
        if (requested.Contains(RefreshSliceNames.Core))
        {
            var coreSnapshot = snapshot!;
            response.Dashboard = ToWireJson(await _financialService.GetDashboardDataAsync(coreSnapshot, false, cancellationToken));
            response.Insights = ToWireJson(await _financialService.GetDashboardInsightsAsync(coreSnapshot.Cycle, cancellationToken));
            var transactions = await BuildCycleTransactionsAsync(coreSnapshot, cancellationToken);
            response.Transactions = transactions.Select(TransactionsController.MapToDto).ToList();
            response.WalletBalance = ToWireJson(await _financialService.GetWalletBalanceAsync(coreSnapshot, cancellationToken));
            response.Accounts = coreSnapshot.LedgerAccounts
                .Select(account => LedgerAccountsController.MapToDto(
                    account,
                    coreSnapshot.LedgerAccountBalances.Current))
                .ToList();
            response.Autocomplete = await _transactionQueryService.GetAutocompleteSuggestionsAsync(cancellationToken);
        }

        if (requested.Contains(RefreshSliceNames.Recurring))
        {
            response.RecurringPayments = await RecurringPaymentsController.BuildRecurringPaymentDtosAsync(
                _recurringPaymentService,
                _payEarlyService,
                cancellationToken);
        }
        if (requested.Contains(RefreshSliceNames.Categories))
        {
            response.Categories = (await _categoryService.GetCategoriesAsync(cancellationToken))
                .Select(TransactionCategoriesController.ToResponse)
                .ToList();
        }
        if (requested.Contains(RefreshSliceNames.Wishlist))
        {
            response.Wishlist = (await _wishlistService.GetWishlistAsync(cancellationToken))
                .Select(WishlistController.MapToDto)
                .ToList();
        }
        if (requested.Contains(RefreshSliceNames.SavingsGoals))
        {
            response.SavingsGoals = (await _savingsGoalService.GetGoalsAsync(cancellationToken))
                .Select(SavingsGoalsController.MapToDto)
                .ToList();
        }
        if (requested.Contains(RefreshSliceNames.Loans))
        {
            response.Loans = (await _loanService.GetLoansAsync(cancellationToken))
                .Select(LoansController.MapToDto)
                .ToList();
        }
        if (requested.Contains(RefreshSliceNames.Investments))
        {
            response.Investments = new BootstrapInvestmentRefreshDto(
                await _investmentPortfolioService.GetAllocationAsync(cancellationToken));
        }
        if (requested.Contains(RefreshSliceNames.Documents))
        {
            var availableYears = await _documentVaultService.GetAvailableTaxYearsAsync(cancellationToken);
            var selectedTaxYear = availableYears.FirstOrDefault();
            var selectedYear = selectedTaxYear == 0 ? (int?)null : selectedTaxYear;
            response.Documents = new BootstrapDocumentsRefreshDto(
                await _documentVaultService.GetUsageAsync(cancellationToken),
                availableYears,
                await _documentRetentionService.GetReviewAsync(cancellationToken),
                selectedYear,
                selectedYear is null
                    ? null
                    : await _documentVaultService.GetTaxYearSummaryAsync(selectedYear.Value, cancellationToken),
                selectedYear is null
                    ? []
                    : await _documentVaultService.GetReliefCategoriesAsync(selectedYear.Value, cancellationToken));
        }

        return Ok(response);
    }

    private async Task<IReadOnlyList<TransactionProjection>> BuildCycleTransactionsAsync(
        FinancialService.FinancialBootstrapSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var transactions = TransactionQueryService.ProjectCycleTransactions(
            snapshot.CycleRelevantTransactions,
            snapshot.Cycle.ActiveYear,
            snapshot.Cycle.ActiveMonthIndex,
            snapshot.Cycle.CycleDay);
        if (!transactions.Items.Any(TransactionQueryService.CanCarryStabilityReloadStatus))
            return transactions.Items;

        var statusMap = await _transactionQueryService.GetStabilityReloadStatusMapAsync(
            cancellationToken,
            transactions.Items.Select(item => item.Id).ToList());
        return TransactionQueryService.ProjectCycleTransactions(
            snapshot.CycleRelevantTransactions,
            snapshot.Cycle.ActiveYear,
            snapshot.Cycle.ActiveMonthIndex,
            snapshot.Cycle.CycleDay,
            statusMap).Items;
    }

    // Mirrors FinancialController's validation so a bad period is rejected identically here.
    private static bool IsValidPeriod(string? month, int? year)
    {
        if (month == null && year == null) return true;
        if (month == null || year == null) return false;
        return year.Value > 0 && FinancialConstants.MonthAbbreviations.Contains(month, StringComparer.Ordinal);
    }

    private static JsonElement ToWireJson(object value) =>
        JsonSerializer.SerializeToElement(value, WireJsonOptions);
}

/// <summary>
/// Nullable properties let System.Text.Json omit every slice the caller did not request while
/// keeping each returned collection strongly typed at the controller boundary.
/// </summary>
public sealed class BootstrapRefreshResponse
{
    public string Month { get; init; } = string.Empty;
    public int Year { get; init; }
    public JsonElement? Dashboard { get; set; }
    public JsonElement? Insights { get; set; }
    public List<TransactionDto>? Transactions { get; set; }
    public List<RecurringPaymentDto>? RecurringPayments { get; set; }
    public List<object>? Categories { get; set; }
    public List<WishlistItemDto>? Wishlist { get; set; }
    public List<SavingsGoalDto>? SavingsGoals { get; set; }
    public List<LoanDto>? Loans { get; set; }
    public List<AutocompleteSuggestion>? Autocomplete { get; set; }
    public JsonElement? WalletBalance { get; set; }
    public List<LedgerAccountDto>? Accounts { get; set; }
    public BootstrapInvestmentRefreshDto? Investments { get; set; }
    public BootstrapDocumentsRefreshDto? Documents { get; set; }
}
