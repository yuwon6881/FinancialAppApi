using Microsoft.AspNetCore.Mvc;
using FinancialAppApi.Database;
using FinancialAppApi.Filters;
using FinancialAppApi.Services;

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
public class BootstrapController : ControllerBase
{
    private readonly FinancialService _financialService;
    private readonly TransactionQueryService _transactionQueryService;
    private readonly RecurringPaymentService _recurringPaymentService;
    private readonly RecurringPaymentPayEarlyService _payEarlyService;
    private readonly TransactionCategoryService _categoryService;
    private readonly WishlistService _wishlistService;
    private readonly Services.SavingsGoals.SavingsGoalService _savingsGoalService;

    public BootstrapController(
        FinancialService financialService,
        TransactionQueryService transactionQueryService,
        RecurringPaymentService recurringPaymentService,
        RecurringPaymentPayEarlyService payEarlyService,
        TransactionCategoryService categoryService,
        WishlistService wishlistService,
        Services.SavingsGoals.SavingsGoalService savingsGoalService)
    {
        _savingsGoalService = savingsGoalService;
        _financialService = financialService;
        _transactionQueryService = transactionQueryService;
        _recurringPaymentService = recurringPaymentService;
        _payEarlyService = payEarlyService;
        _categoryService = categoryService;
        _wishlistService = wishlistService;
    }

    // GET: api/bootstrap?month=Jul&year=2026
    [HttpGet]
    public async Task<ActionResult<object>> GetBootstrap(
        [FromQuery(Name = "month")] string? queryMonth = null,
        [FromQuery(Name = "year")] int? queryYear = null,
        [FromQuery(Name = "persistSelection")] bool persistSelection = true)
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

        var stabilityReloadStatuses = await _transactionQueryService.GetStabilityReloadStatusMapAsync(cancellationToken);
        var transactions = TransactionQueryService.ProjectCycleTransactions(
            snapshot.CycleRelevantTransactions,
            snapshot.Cycle.ActiveYear,
            snapshot.Cycle.ActiveMonthIndex,
            snapshot.Cycle.CycleDay,
            stabilityReloadStatuses);

        var recurringPayments = await RecurringPaymentsController.BuildRecurringPaymentDtosAsync(
            _recurringPaymentService, _payEarlyService, cancellationToken);

        var categories = await _categoryService.GetCategoriesAsync(cancellationToken);
        var wishlist = await _wishlistService.GetWishlistAsync(cancellationToken);
        var savingsGoals = await _savingsGoalService.GetGoalsAsync(cancellationToken);
        var autocomplete = await _transactionQueryService.GetAutocompleteSuggestionsAsync(cancellationToken);
        var walletBalance = await _financialService.GetWalletBalanceAsync(snapshot, cancellationToken);

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
            autocomplete,
            walletBalance,
        });
    }

    // Mirrors FinancialController's validation so a bad period is rejected identically here.
    private static bool IsValidPeriod(string? month, int? year)
    {
        if (month == null && year == null) return true;
        if (month == null || year == null) return false;
        return FinancialConstants.MonthAbbreviations.Contains(month, StringComparer.Ordinal);
    }
}
