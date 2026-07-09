using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;
using FinancialAppApi.Services;
using System.Globalization;
using System.Text;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/transactions")]
[AuthorizeToken]
public class TransactionsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly TransactionPersistenceService _transactionPersistenceService;

    public TransactionsController(
        AppDbContext context,
        TransactionPersistenceService transactionPersistenceService)
    {
        _context = context;
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
        // page=0/negative would otherwise produce a negative Skip() offset,
        // which Npgsql rejects with an unhandled 500 instead of a clean result.
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        if (all)
        {
            var query = ApplyAllFilters(_context.Transactions.AsNoTracking(), search, ledgerCategory, category, txType, startDate, endDate);
            var total = await query.CountAsync();

            var txs = await query
                .OrderByDescending(t => t.Date)
                .ThenByDescending(t => t.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Ok(new
            {
                items = txs.Select(MapToDto).ToList(),
                total,
                page,
                pageSize
            });
        }

        var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
        if (setting == null)
        {
            var txs = await _context.Transactions
                .AsNoTracking()
                .OrderByDescending(t => t.Date)
                .ThenByDescending(t => t.Id)
                .ToListAsync();
            return Ok(txs.Select(MapToDto).ToList());
        }

        string activeMonth = queryMonth ?? setting.SelectedMonth;
        int activeYear = queryYear ?? setting.SelectedYear;

        var activeMonthIndex = Array.IndexOf(FinancialConstants.MonthAbbreviations, activeMonth) + 1;
        if (activeMonthIndex == 0) activeMonthIndex = 6;

        var (cycleStart, cycleEnd, _) = CategoryAttributionService.GetCycleRange(activeYear, activeMonthIndex, setting.CycleDay);
        var cycleStartDate = TransactionDate.StartOfDate(DateOnly.FromDateTime(cycleStart));
        var cycleEndExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(cycleEnd));

        var filtered = await _context.Transactions
            .AsNoTracking()
            .Where(t => t.LedgerCategory.ToUpper() != "DISCARDED" && t.Date >= cycleStartDate && t.Date < cycleEndExclusive)
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.Id)
            .ToListAsync();

        return Ok(filtered.Select(MapToDto).ToList());
    }

    // GET: api/transactions/autocomplete
    [HttpGet("autocomplete")]
    public async Task<IActionResult> GetAutocompleteSuggestions()
    {
        var recentTxs = await _context.Transactions
            .Where(t => t.LedgerCategory != "Discarded")
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.Id)
            .Take(1000)
            .Select(t => new { t.Description, t.Category, t.LedgerCategory, t.Amount })
            .ToListAsync();

        var suggestions = new List<AutocompleteSuggestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tx in recentTxs)
        {
            var txType = tx.Amount >= 0 ? "inflow" : "outflow";
            var key = $"{txType}:{tx.Description.Trim()}";

            if (!seen.Contains(key))
            {
                seen.Add(key);
                suggestions.Add(new AutocompleteSuggestion
                {
                    Description = tx.Description.Trim(),
                    Category = tx.Category,
                    LedgerCategory = tx.LedgerCategory,
                    TxType = txType
                });
            }
        }

        return Ok(suggestions);
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
        var query = ApplyAllFilters(_context.Transactions.AsNoTracking(), search, ledgerCategory, category, txType, startDate, endDate);
        var rows = await query
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.Id)
            .Select(t => new { t.Date, t.Description, t.Category, t.LedgerCategory, t.Amount })
            .ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("Date,Description,Category,Ledger Category,Debit (Outflow),Credit (Inflow)");

        foreach (var t in rows)
        {
            var isTransfer = t.LedgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase);
            var isOutflow = t.Amount < 0;
            var debit = isTransfer ? FormatAmount(t.Amount) : (isOutflow ? FormatAmount(Math.Abs(t.Amount)) : "");
            var credit = isTransfer ? FormatAmount(t.Amount) : (!isOutflow ? FormatAmount(t.Amount) : "");

            sb.AppendLine(string.Join(",", new[]
            {
                EscapeCsvField(TransactionDate.ToDateOnly(t.Date).ToString("yyyy-MM-dd")),
                EscapeCsvField(t.Description),
                EscapeCsvField(t.Category),
                EscapeCsvField(DisplayLedgerCategory(t.LedgerCategory)),
                EscapeCsvField(debit),
                EscapeCsvField(credit)
            }));
        }

        var csvBytes = Encoding.UTF8.GetBytes(sb.ToString());
        var preamble = Encoding.UTF8.GetPreamble();
        var output = new byte[preamble.Length + csvBytes.Length];
        Buffer.BlockCopy(preamble, 0, output, 0, preamble.Length);
        Buffer.BlockCopy(csvBytes, 0, output, preamble.Length, csvBytes.Length);

        var fileName = $"financial_ledger_{DateTime.Now:yyyy-MM-dd}.csv";
        return File(output, "text/csv", fileName);
    }

    // POST: api/transactions
    [HttpPost]
    public async Task<ActionResult<TransactionDto>> PostTransaction(TransactionDto dto)
    {
        var result = await _transactionPersistenceService.CreateTransactionAsync(ToMutationRequest(dto));
        if (result.Status == TransactionMutationStatus.InvalidDate)
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
        if (result.Status == TransactionMutationStatus.InvalidDate)
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

    private static bool TryParseDate(string? value, out DateOnly date)
    {
        return TransactionDate.TryParseInputDate(value, out date);
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

    private static TransactionDto MapToDto(Transaction t)
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

    private static IQueryable<Transaction> ApplyAllFilters(
        IQueryable<Transaction> query,
        string? search,
        string? ledgerCategory,
        string? category,
        string? txType,
        string? startDate,
        string? endDate)
    {
        // Exclude Discarded transactions from ledger listings
        query = query.Where(t => t.LedgerCategory != "Discarded");

        if (TryParseDate(startDate, out var startDateOnly))
        {
            query = query.Where(t => t.Date >= TransactionDate.StartOfDate(startDateOnly));
        }
        if (TryParseDate(endDate, out var endDateOnly))
        {
            query = query.Where(t => t.Date < TransactionDate.ExclusiveEndOfDate(endDateOnly));
        }

        // Search: description, category, ledgerCategory
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(t =>
                t.Description.ToLower().Contains(s) ||
                t.Category.ToLower().Contains(s) ||
                t.LedgerCategory.ToLower().Contains(s));
        }

        // Ledger category filter (comma-separated e.g. "Growth,Rewards")
        if (!string.IsNullOrWhiteSpace(ledgerCategory))
        {
            var buckets = ledgerCategory.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => b.Trim().ToLower()).ToList();

            query = query.Where(t =>
                buckets.Any(bucket =>
                    bucket == "income"
                        ? (t.LedgerCategory.ToLower() == "income" || t.LedgerCategory.ToLower().StartsWith("incomesplit:"))
                        : (t.LedgerCategory.ToLower() == bucket || t.LedgerCategory.ToLower().Contains(bucket))));
        }

        // Subcategory filter (comma-separated)
        if (!string.IsNullOrWhiteSpace(category))
        {
            var cats = category.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim().ToLower()).ToList();
            query = query.Where(t => cats.Contains(t.Category.ToLower()));
        }

        // Inflow / Outflow filter
        if (!string.IsNullOrWhiteSpace(txType))
        {
            if (txType == "inflow")
                query = query.Where(t => t.Amount > 0);
            else if (txType == "outflow")
                query = query.Where(t => t.Amount < 0);
        }

        return query;
    }

    private static string EscapeCsvField(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    private static string FormatAmount(decimal amount)
    {
        return amount.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string DisplayLedgerCategory(string ledgerCategory)
    {
        if (ledgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase)) return "Income";
        if (ledgerCategory.StartsWith("Transfer:Income->", StringComparison.OrdinalIgnoreCase))
        {
            return ledgerCategory.Substring("Transfer:Income->".Length);
        }
        if (ledgerCategory.StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase)) return "Transfer";
        return ledgerCategory;
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
