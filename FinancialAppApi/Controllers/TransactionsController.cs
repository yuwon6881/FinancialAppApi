using Microsoft.AspNetCore.Mvc;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;
using FinancialAppApi.Services;
using FinancialAppApi.Contracts;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/transactions")]
[AuthorizeToken]
[RefreshSlices(RefreshSliceNames.Core)]
public class TransactionsController : ControllerBase
{
    private readonly TransactionQueryService _transactionQueryService;
    private readonly TransactionPersistenceService _transactionPersistenceService;

    public TransactionsController(
        TransactionQueryService transactionQueryService,
        TransactionPersistenceService transactionPersistenceService)
    {
        _transactionQueryService = transactionQueryService;
        _transactionPersistenceService = transactionPersistenceService;
    }

    // GET: api/transactions
    [HttpGet]
    public async Task<IActionResult> GetTransactions(
        [FromQuery(Name = "month")] string? queryMonth = null,
        [FromQuery(Name = "year")] int? queryYear = null,
        [FromQuery(Name = "all")] bool all = false,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "pageSize")] int pageSize = 10,
        [FromQuery(Name = "search")] string? search = null,
        [FromQuery(Name = "searchMode")] string? searchMode = null,
        [FromQuery(Name = "ledgerCategory")] string? ledgerCategory = null,
        [FromQuery(Name = "category")] string? category = null,
        [FromQuery(Name = "txType")] string? txType = null,
        [FromQuery(Name = "startDate")] string? startDate = null,
        [FromQuery(Name = "endDate")] string? endDate = null,
        [FromQuery(Name = "minAmount")] decimal? minAmount = null,
        [FromQuery(Name = "maxAmount")] decimal? maxAmount = null,
        [FromQuery(Name = "recurringOnly")] bool recurringOnly = false,
        [FromQuery(Name = "wishlistOnly")] bool wishlistOnly = false,
        [FromQuery(Name = "sort")] string? sort = null,
        [FromQuery(Name = "recurringFilter")] string? recurringFilter = null,
        [FromQuery(Name = "wishlistFilter")] string? wishlistFilter = null,
        [FromQuery(Name = "reloadFilter")] string? reloadFilter = null,
        [FromQuery(Name = "accountId")] string? accountId = null)
    {
        if (queryMonth != null && !FinancialConstants.MonthAbbreviations.Contains(queryMonth, StringComparer.Ordinal))
            return BadRequest(new { message = "Month must be a valid three-letter abbreviation." });

        var result = await _transactionQueryService.GetTransactionsAsync(
            queryMonth,
            queryYear,
            all,
            page,
            pageSize,
            search,
            searchMode,
            ledgerCategory,
            category,
            txType,
            startDate,
            endDate,
            minAmount,
            maxAmount,
            recurringOnly,
            wishlistOnly,
            sort,
            recurringFilter,
            wishlistFilter,
            reloadFilter,
            accountId,
            HttpContext.RequestAborted);

        if (result.Total.HasValue)
        {
            return Ok(new
            {
                items = result.Items.Select(MapToDto).ToList(),
                total = result.Total.Value,
                page = result.Page!.Value,
                pageSize = result.PageSize!.Value
            });
        }

        return Ok(result.Items.Select(MapToDto).ToList());
    }

    // GET: api/transactions/autocomplete
    [HttpGet("autocomplete")]
    public async Task<IActionResult> GetAutocompleteSuggestions()
    {
        return Ok(await _transactionQueryService.GetAutocompleteSuggestionsAsync(HttpContext.RequestAborted));
    }

    // GET: api/transactions/{id}
    [HttpGet("{id}")]
    public async Task<ActionResult<TransactionDto>> GetTransaction(string id)
    {
        var transaction = await _transactionQueryService.GetTransactionByIdAsync(id, HttpContext.RequestAborted);
        if (transaction == null)
        {
            return NotFound();
        }

        var reloadStatus = TransactionQueryService.CanCarryStabilityReloadStatus(new TransactionProjection(
            transaction.Id,
            transaction.Date,
            transaction.PostedAt,
            transaction.Description,
            transaction.Category,
            transaction.LedgerCategory,
            transaction.Amount,
            transaction.StabilityRecoveryTopUpAmount,
            transaction.StabilityReloadIntent,
            transaction.RecurringPaymentId,
            transaction.RecurringOccurrenceDate,
            transaction.WishlistItemId,
            transaction.SavingsGoalId,
            null,
            transaction.AccountId,
            transaction.CounterAccountId,
            transaction.ExcludeFromAutocomplete,
            transaction.IsAccountBalanceAdjustment))
            ? (await _transactionQueryService.GetStabilityReloadStatusMapAsync(
                HttpContext.RequestAborted,
                [transaction.Id])).GetValueOrDefault(transaction.Id)
            : null;

        return Ok(MapToDto(transaction, reloadStatus));
    }

    // GET: api/transactions/export
    [HttpGet("export")]
    public async Task<IActionResult> ExportTransactions(
        [FromQuery(Name = "search")] string? search = null,
        [FromQuery(Name = "searchMode")] string? searchMode = null,
        [FromQuery(Name = "ledgerCategory")] string? ledgerCategory = null,
        [FromQuery(Name = "category")] string? category = null,
        [FromQuery(Name = "txType")] string? txType = null,
        [FromQuery(Name = "startDate")] string? startDate = null,
        [FromQuery(Name = "endDate")] string? endDate = null,
        [FromQuery(Name = "minAmount")] decimal? minAmount = null,
        [FromQuery(Name = "maxAmount")] decimal? maxAmount = null,
        [FromQuery(Name = "recurringOnly")] bool recurringOnly = false,
        [FromQuery(Name = "wishlistOnly")] bool wishlistOnly = false,
        [FromQuery(Name = "recurringFilter")] string? recurringFilter = null,
        [FromQuery(Name = "wishlistFilter")] string? wishlistFilter = null,
        [FromQuery(Name = "reloadFilter")] string? reloadFilter = null,
        [FromQuery(Name = "accountId")] string? accountId = null,
        [FromQuery(Name = "sort")] string? sort = null)
    {
        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition =
            $"attachment; filename=\"{_transactionQueryService.GetTransactionsExportFileName()}\"";
        await _transactionQueryService.WriteTransactionsCsvAsync(
            Response.Body,
            search,
            searchMode,
            ledgerCategory,
            category,
            txType,
            startDate,
            endDate,
            minAmount,
            maxAmount,
            recurringOnly,
            wishlistOnly,
            recurringFilter,
            wishlistFilter,
            sort,
            reloadFilter,
            accountId,
            HttpContext.RequestAborted);
        return new EmptyResult();
    }

    // POST: api/transactions
    [HttpPost]
    public async Task<ActionResult<TransactionDto>> PostTransaction(TransactionDto dto)
    {
        var result = await _transactionPersistenceService.CreateTransactionAsync(
            ToMutationRequest(dto),
            HttpContext.RequestAborted);
        if (result.Status is TransactionMutationStatus.InvalidDate or TransactionMutationStatus.InvalidAmount)
        {
            return BadRequest(new { message = result.Message });
        }
        if (result.Status is TransactionMutationStatus.InvalidCategory
            or TransactionMutationStatus.InvalidLedgerCategory
            or TransactionMutationStatus.InvalidAccount)
        {
            return BadRequest(new { code = result.Code, message = result.Message, missingBuckets = result.MissingBuckets });
        }
        if (result.Status == TransactionMutationStatus.InvalidRecurringOccurrence)
        {
            return BadRequest(new { message = result.Message });
        }
        if (result.Status == TransactionMutationStatus.Conflict)
        {
            return Conflict(new { message = result.Message });
        }
        if (result.Status == TransactionMutationStatus.Existing)
        {
            var statusMap = await _transactionQueryService.GetStabilityReloadStatusMapAsync(
                HttpContext.RequestAborted);
            return Ok(MapToDto(
                result.Transaction!,
                statusMap.TryGetValue(result.Transaction!.Id, out var status) ? status : null));
        }

        var createdStatuses = await _transactionQueryService.GetStabilityReloadStatusMapAsync(
            HttpContext.RequestAborted);
        return CreatedAtAction(
            nameof(GetTransactions),
            new { id = result.Transaction!.Id },
            MapToDto(
                result.Transaction,
                createdStatuses.TryGetValue(result.Transaction.Id, out var createdStatus)
                    ? createdStatus
                    : null));
    }

    [HttpPost("bulk-create")]
    public async Task<IActionResult> BulkCreateTransactions(
        [FromBody] BulkCreateTransactionsRequest? request)
    {
        var transactions = request?.Transactions ?? [];
        if (transactions.Count < 1) return BadRequest(new { message = "Choose at least one transaction to create." });
        if (transactions.Count > 100) return BadRequest(new { message = "Create at most 100 transactions at a time." });

        var result = await _transactionPersistenceService.CreateTransactionsAsync(
            transactions.Select(ToMutationRequest).ToList(),
            HttpContext.RequestAborted);

        if (result.Status == TransactionMutationStatus.Conflict)
        {
            return Conflict(new { message = result.Message, index = result.FailedIndex });
        }
        if (result.Status is TransactionMutationStatus.InvalidDate
            or TransactionMutationStatus.InvalidAmount
            or TransactionMutationStatus.InvalidRecurringOccurrence)
        {
            return BadRequest(new { message = result.Message, index = result.FailedIndex });
        }
        if (result.Status is TransactionMutationStatus.InvalidCategory
            or TransactionMutationStatus.InvalidLedgerCategory
            or TransactionMutationStatus.InvalidAccount)
        {
            return BadRequest(new { code = result.Code, message = result.Message, missingBuckets = result.MissingBuckets, index = result.FailedIndex });
        }

        return Ok(new { created = result.Transactions.Select(t => MapToDto(t)).ToList() });
    }

    [HttpPost("bulk-delete")]
    public async Task<IActionResult> BulkDeleteTransactions(
        [FromBody] BulkDeleteTransactionsRequest? request)
    {
        var ids = request?.Ids?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Select(CanonicalTransactionId)
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];
        if (ids.Count == 0) return BadRequest(new { message = "Choose at least one transaction." });
        if (ids.Count > 100) return BadRequest(new { message = "Delete at most 100 transactions at a time." });

        var result = await _transactionPersistenceService.DeleteTransactionsAsync(ids, HttpContext.RequestAborted);
        if (result.Status == TransactionMutationStatus.Conflict)
        {
            return Conflict(new { message = result.Message });
        }
        if (result.Status == TransactionMutationStatus.InvalidAmount)
        {
            return BadRequest(new { message = result.Message });
        }

        return Ok(new { deleted = result.Transactions.Select(transaction => MapToDto(transaction)).ToList() });
    }

    [HttpPost("bulk-restore")]
    public async Task<IActionResult> BulkRestoreTransactions(
        [FromBody] BulkRestoreTransactionsRequest? request)
    {
        var transactions = request?.Transactions ?? [];
        if (transactions.Count == 0) return BadRequest(new { message = "Choose at least one transaction to restore." });
        if (transactions.Count > 100) return BadRequest(new { message = "Restore at most 100 transactions at a time." });
        if (transactions.Any(transaction => transaction.SavingsGoalId.HasValue))
        {
            return Conflict(new { message = "Commitment completions must be restored through their individual undo flow." });
        }

        var result = await _transactionPersistenceService.RestoreTransactionsAsync(
            transactions.Select(ToMutationRequest).ToList(),
            HttpContext.RequestAborted);
        if (result.Status is TransactionMutationStatus.Conflict)
        {
            return Conflict(new { message = result.Message });
        }
        if (result.Status is TransactionMutationStatus.InvalidDate
            or TransactionMutationStatus.InvalidAmount
            or TransactionMutationStatus.InvalidCategory
            or TransactionMutationStatus.InvalidLedgerCategory
            or TransactionMutationStatus.InvalidAccount
            or TransactionMutationStatus.InvalidRecurringOccurrence)
        {
            return BadRequest(new { message = result.Message });
        }

        return Ok(new { restored = result.Transactions.Select(transaction => MapToDto(transaction)).ToList() });
    }

    [HttpPost("bulk-move")]
    public async Task<IActionResult> BulkMoveTransactions([FromBody] BulkMoveTransactionsRequest? request)
    {
        var moves = request?.Moves ?? [];
        if (moves.Count is < 1 or > 100)
            return BadRequest(new { message = "Move between 1 and 100 transactions at a time." });
        var result = await _transactionPersistenceService.MoveTransactionsAsync(
            moves.Select(move => new TransactionMoveRequest(move.Id, move.TargetDate)).ToList(),
            HttpContext.RequestAborted);
        if (result.Status == TransactionMutationStatus.Conflict)
            return Conflict(new { message = result.Message });
        return Ok(new
        {
            moved = result.Transactions.Select(transaction => MapToDto(transaction)).ToList(),
            affectedDates = result.AffectedDates
        });
    }

    private static string CanonicalTransactionId(string id)
    {
        var splitIndex = id.IndexOf("-split-", StringComparison.Ordinal);
        return splitIndex < 0 ? id : id[..splitIndex];
    }

    // PUT: api/transactions/{id}
    [HttpPut("{id}")]
    public async Task<IActionResult> PutTransaction(string id, TransactionDto dto)
    {
        var result = await _transactionPersistenceService.UpdateTransactionAsync(
            id,
            ToMutationRequest(dto),
            HttpContext.RequestAborted);
        if (result.Status == TransactionMutationStatus.NotFound)
        {
            return NotFound();
        }
        if (result.Status == TransactionMutationStatus.Conflict)
        {
            return Conflict(new { message = result.Message });
        }
        if (result.Status is TransactionMutationStatus.InvalidDate or TransactionMutationStatus.InvalidAmount)
        {
            return BadRequest(new { message = result.Message });
        }
        if (result.Status is TransactionMutationStatus.InvalidCategory
            or TransactionMutationStatus.InvalidLedgerCategory
            or TransactionMutationStatus.InvalidAccount)
        {
            return BadRequest(new { code = result.Code, message = result.Message, missingBuckets = result.MissingBuckets });
        }

        return NoContent();
    }

    // DELETE: api/transactions/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteTransaction(string id)
    {
        var result = await _transactionPersistenceService.DeleteTransactionAsync(
            id,
            HttpContext.RequestAborted);
        if (result.Status == TransactionMutationStatus.NotFound)
        {
            return NotFound();
        }
        if (result.Status == TransactionMutationStatus.Conflict)
        {
            return Conflict(new { message = result.Message });
        }

        return NoContent();
    }

    private static TransactionMutationRequest ToMutationRequest(TransactionDto dto)
    {
        return new TransactionMutationRequest(
            dto.Id,
            dto.Date,
            dto.PostedAt,
            dto.Description,
            dto.Category,
            dto.LedgerCategory,
            dto.Amount,
            dto.RecurringPaymentId,
            dto.WishlistItemId,
            dto.RecurringOccurrenceDate,
            dto.StabilityRecoveryTopUpAmount,
            dto.StabilityReloadIntent,
            dto.AccountId,
            dto.CounterAccountId,
            dto.SplitAccountIds);
    }

    public static TransactionDto MapToDto(Transaction t, string? stabilityReloadStatus = null)
    {
        return new TransactionDto
        {
            Id = t.Id,
            Date = TransactionDate.ToDateOnly(t.Date).ToString("yyyy-MM-dd"),
            PostedAt = t.PostedAt.ToUniversalTime().ToString("O"),
            Description = t.Description,
            Category = t.Category,
            LedgerCategory = t.LedgerCategory,
            Amount = ObfuscationHelper.Obfuscate(t.Amount),
            AccountId = t.AccountId,
            CounterAccountId = t.CounterAccountId,
            StabilityRecoveryTopUpAmount = t.StabilityRecoveryTopUpAmount.HasValue
                ? ObfuscationHelper.Obfuscate(t.StabilityRecoveryTopUpAmount.Value)
                : null,
            StabilityReloadIntent = t.StabilityReloadIntent,
            RecurringPaymentId = t.RecurringPaymentId,
            RecurringOccurrenceDate = t.RecurringOccurrenceDate?.ToString("yyyy-MM-dd"),
            WishlistItemId = t.WishlistItemId,
            SavingsGoalId = t.SavingsGoalId,
            ExcludeFromAutocomplete = t.ExcludeFromAutocomplete,
            IsAccountBalanceAdjustment = t.IsAccountBalanceAdjustment,
            StabilityReloadStatus = stabilityReloadStatus
        };
    }

    public static TransactionDto MapToDto(TransactionProjection t)
    {
        return new TransactionDto
        {
            Id = t.Id,
            Date = TransactionDate.ToDateOnly(t.Date).ToString("yyyy-MM-dd"),
            PostedAt = t.PostedAt.ToUniversalTime().ToString("O"),
            Description = t.Description,
            Category = t.Category,
            LedgerCategory = t.LedgerCategory,
            Amount = ObfuscationHelper.Obfuscate(t.Amount),
            AccountId = t.AccountId,
            CounterAccountId = t.CounterAccountId,
            StabilityRecoveryTopUpAmount = t.StabilityRecoveryTopUpAmount.HasValue
                ? ObfuscationHelper.Obfuscate(t.StabilityRecoveryTopUpAmount.Value)
                : null,
            StabilityReloadIntent = t.StabilityReloadIntent,
            RecurringPaymentId = t.RecurringPaymentId,
            RecurringOccurrenceDate = t.RecurringOccurrenceDate?.ToString("yyyy-MM-dd"),
            WishlistItemId = t.WishlistItemId,
            SavingsGoalId = t.SavingsGoalId,
            ExcludeFromAutocomplete = t.ExcludeFromAutocomplete,
            IsAccountBalanceAdjustment = t.IsAccountBalanceAdjustment,
            StabilityReloadStatus = t.StabilityReloadStatus
        };
    }
}

public class TransactionDto
{
    public string? Id { get; set; }
    public string Date { get; set; } = string.Empty;
    public string? PostedAt { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string LedgerCategory { get; set; } = string.Empty;
    public string Amount { get; set; } = string.Empty;
    public string? AccountId { get; set; }
    public string? CounterAccountId { get; set; }
    public Dictionary<string, string>? SplitAccountIds { get; set; }
    public string? StabilityRecoveryTopUpAmount { get; set; }
    public string? StabilityReloadIntent { get; set; }
    public string? RecurringPaymentId { get; set; }
    public string? RecurringOccurrenceDate { get; set; }
    public int? WishlistItemId { get; set; }
    public int? SavingsGoalId { get; set; }
    public bool ExcludeFromAutocomplete { get; set; }
    public bool IsAccountBalanceAdjustment { get; set; }
    public string? StabilityReloadStatus { get; set; }
}

public sealed class BulkDeleteTransactionsRequest
{
    public List<string> Ids { get; set; } = [];
}

public sealed class BulkRestoreTransactionsRequest
{
    public List<TransactionDto> Transactions { get; set; } = [];
}

public sealed class BulkMoveTransactionsRequest
{
    public List<BulkMoveTransactionItem> Moves { get; set; } = [];
}

public sealed class BulkMoveTransactionItem
{
    public string Id { get; set; } = string.Empty;
    public string TargetDate { get; set; } = string.Empty;
}

public sealed class BulkCreateTransactionsRequest
{
    public List<TransactionDto> Transactions { get; set; } = [];
}
