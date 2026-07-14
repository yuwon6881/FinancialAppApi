using Microsoft.AspNetCore.Mvc;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;
using FinancialAppApi.Services;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/transactions")]
[AuthorizeToken]
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
        [FromQuery(Name = "ledgerCategory")] string? ledgerCategory = null,
        [FromQuery(Name = "category")] string? category = null,
        [FromQuery(Name = "txType")] string? txType = null,
        [FromQuery(Name = "startDate")] string? startDate = null,
        [FromQuery(Name = "endDate")] string? endDate = null)
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
            ledgerCategory,
            category,
            txType,
            startDate,
            endDate,
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

        return Ok(MapToDto(transaction));
    }

    // GET: api/transactions/export
    [HttpGet("export")]
    public async Task<IActionResult> ExportTransactions(
        [FromQuery(Name = "search")] string? search = null,
        [FromQuery(Name = "ledgerCategory")] string? ledgerCategory = null,
        [FromQuery(Name = "category")] string? category = null,
        [FromQuery(Name = "txType")] string? txType = null,
        [FromQuery(Name = "startDate")] string? startDate = null,
        [FromQuery(Name = "endDate")] string? endDate = null)
    {
        var export = await _transactionQueryService.ExportTransactionsAsync(search, ledgerCategory, category, txType, startDate, endDate, HttpContext.RequestAborted);
        return File(export.Bytes, "text/csv", export.FileName);
    }

    // POST: api/transactions
    [HttpPost]
    public async Task<ActionResult<TransactionDto>> PostTransaction(TransactionDto dto)
    {
        var result = await _transactionPersistenceService.CreateTransactionAsync(ToMutationRequest(dto));
        if (result.Status is TransactionMutationStatus.InvalidDate or TransactionMutationStatus.InvalidAmount)
        {
            return BadRequest(new { message = result.Message });
        }
        if (result.Status is TransactionMutationStatus.InvalidCategory or TransactionMutationStatus.InvalidLedgerCategory)
        {
            return BadRequest(new { message = result.Message });
        }
        if (result.Status == TransactionMutationStatus.Existing)
        {
            return Ok(MapToDto(result.Transaction!));
        }

        return CreatedAtAction(nameof(GetTransactions), new { id = result.Transaction!.Id }, MapToDto(result.Transaction));
    }

    // PUT: api/transactions/{id}
    [HttpPut("{id}")]
    public async Task<IActionResult> PutTransaction(string id, TransactionDto dto)
    {
        var result = await _transactionPersistenceService.UpdateTransactionAsync(id, ToMutationRequest(dto));
        if (result.Status == TransactionMutationStatus.NotFound)
        {
            return NotFound();
        }
        if (result.Status is TransactionMutationStatus.InvalidDate or TransactionMutationStatus.InvalidAmount)
        {
            return BadRequest(new { message = result.Message });
        }
        if (result.Status is TransactionMutationStatus.InvalidCategory or TransactionMutationStatus.InvalidLedgerCategory)
        {
            return BadRequest(new { message = result.Message });
        }

        return NoContent();
    }

    // DELETE: api/transactions/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteTransaction(string id)
    {
        var result = await _transactionPersistenceService.DeleteTransactionAsync(id);
        if (result.Status == TransactionMutationStatus.NotFound)
        {
            return NotFound();
        }

        return NoContent();
    }

    private static TransactionMutationRequest ToMutationRequest(TransactionDto dto)
    {
        return new TransactionMutationRequest(
            dto.Id,
            dto.Date,
            dto.Description,
            dto.Category,
            dto.LedgerCategory,
            dto.Amount,
            dto.RecurringPaymentId,
            dto.WishlistItemId);
    }

    public static TransactionDto MapToDto(Transaction t)
    {
        return new TransactionDto
        {
            Id = t.Id,
            Date = TransactionDate.ToDateOnly(t.Date).ToString("yyyy-MM-dd"),
            PostedAt = t.Date.ToUniversalTime().ToString("O"),
            Description = t.Description,
            Category = t.Category,
            LedgerCategory = t.LedgerCategory,
            Amount = ObfuscationHelper.Obfuscate(t.Amount),
            RecurringPaymentId = t.RecurringPaymentId,
            WishlistItemId = t.WishlistItemId
        };
    }

    public static TransactionDto MapToDto(TransactionProjection t)
    {
        return new TransactionDto
        {
            Id = t.Id,
            Date = TransactionDate.ToDateOnly(t.Date).ToString("yyyy-MM-dd"),
            PostedAt = t.Date.ToUniversalTime().ToString("O"),
            Description = t.Description,
            Category = t.Category,
            LedgerCategory = t.LedgerCategory,
            Amount = ObfuscationHelper.Obfuscate(t.Amount),
            RecurringPaymentId = t.RecurringPaymentId,
            WishlistItemId = t.WishlistItemId
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
    public string? RecurringPaymentId { get; set; }
    public int? WishlistItemId { get; set; }
}
