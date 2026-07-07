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
    private readonly CycleBalanceService _cycleBalanceService;

    public TransactionsController(AppDbContext context, CycleBalanceService cycleBalanceService)
    {
        _context = context;
        _cycleBalanceService = cycleBalanceService;
    }

    // Invalidates every cached cycle balance snapshot at or after the cycle a given transaction
    // date falls in -- a no-op if settings (and therefore CycleDay) don't exist yet, since in
    // that case no snapshots have ever been computed either.
    private async Task InvalidateCycleBalancesFromAsync(DateOnly date)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
        if (setting == null) return;

        var (year, monthIndex) = FinancialController.GetCycleYearAndMonthIndexForDate(date, setting.CycleDay);
        await _cycleBalanceService.InvalidateFromAsync(year, monthIndex);
    }

    // Persists the pending transaction change and invalidates the affected cycle balance cache
    // as one atomic database transaction. This app can be signed in on multiple devices at once,
    // and the cache lives in the shared database (not per-device) -- without this, a dashboard
    // read from another device landing in the gap between "transaction saved" and "cache
    // invalidated" (two otherwise-separate commits) could see the new transaction data paired
    // with a stale, not-yet-invalidated balance. Wrapping both in one transaction means any
    // reader under Postgres's default Read Committed isolation sees either the fully-old state
    // or the fully-new-and-invalidated state, never the in-between.
    //
    // Must go through CreateExecutionStrategy().ExecuteAsync(...) rather than a bare
    // BeginTransactionAsync() -- Program.cs enables Npgsql's EnableRetryOnFailure(), and its
    // retrying execution strategy refuses to run a user-started transaction directly (it needs
    // to own the whole retry unit so it can safely replay it from scratch on a transient failure).
    private async Task SaveAndInvalidateCycleBalancesAsync(DateOnly earliestAffectedDate)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync();
            await _context.SaveChangesAsync();
            await InvalidateCycleBalancesFromAsync(earliestAffectedDate);
            await dbTransaction.CommitAsync();
        });
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
            var query = ApplyAllFilters(_context.Transactions.AsQueryable(), search, ledgerCategory, category, txType, startDate, endDate);
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
                .OrderByDescending(t => t.Date)
                .ThenByDescending(t => t.Id)
                .ToListAsync();
            return Ok(txs.Select(MapToDto).ToList());
        }

        string activeMonth = queryMonth ?? setting.SelectedMonth;
        int activeYear = queryYear ?? setting.SelectedYear;

        var activeMonthIndex = Array.IndexOf(FinancialConstants.MonthAbbreviations, activeMonth) + 1;
        if (activeMonthIndex == 0) activeMonthIndex = 6;

        var (cycleStart, cycleEnd, _) = FinancialController.GetCycleRange(activeYear, activeMonthIndex, setting.CycleDay);
        var cycleStartDate = DateOnly.FromDateTime(cycleStart);
        var cycleEndDate = DateOnly.FromDateTime(cycleEnd);

        var filtered = await _context.Transactions
            .Where(t => t.LedgerCategory.ToUpper() != "DISCARDED" && t.Date >= cycleStartDate && t.Date <= cycleEndDate)
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
                EscapeCsvField(t.Date.ToString("yyyy-MM-dd")),
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
        if (!string.IsNullOrWhiteSpace(dto.Id))
        {
            var existingTx = await _context.Transactions.FirstOrDefaultAsync(t => t.Id == dto.Id);
            if (existingTx != null)
            {
                return Ok(MapToDto(existingTx));
            }
        }

        if (!TryParseDate(dto.Date, out var postDate))
        {
            return BadRequest(new { message = "Date must be in yyyy-MM-dd format." });
        }

        var transaction = new Transaction
        {
            Id = string.IsNullOrWhiteSpace(dto.Id) ? $"tx-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" : dto.Id,
            Date = postDate,
            Description = dto.Description,
            Category = dto.Category,
            LedgerCategory = dto.LedgerCategory,
            Amount = Math.Round(ObfuscationHelper.Deobfuscate(dto.Amount), 2, MidpointRounding.AwayFromZero),
            RecurringPaymentId = dto.RecurringPaymentId
        };

        var splitSpec = await ResolveIncomeSplitSpecAsync(transaction);

        _context.Transactions.Add(transaction);
        AddIncomeSplitTransactions(transaction, splitSpec);

        await SaveAndInvalidateCycleBalancesAsync(transaction.Date);

        return CreatedAtAction(nameof(GetTransactions), new { id = transaction.Id }, MapToDto(transaction));
    }

    // PUT: api/transactions/{id}
    [HttpPut("{id}")]
    public async Task<IActionResult> PutTransaction(string id, TransactionDto dto)
    {
        var transaction = await _context.Transactions.FindAsync(id);
        if (transaction == null)
        {
            return NotFound();
        }

        if (!TryParseDate(dto.Date, out var putDate))
        {
            return BadRequest(new { message = "Date must be in yyyy-MM-dd format." });
        }

        // A backdated edit (date moved earlier) or a forward-dated correction (date moved later)
        // both invalidate every cached cycle balance from whichever date is earlier -- the old
        // cycle's running total no longer includes this transaction, and the new cycle's does.
        var originalDate = transaction.Date;

        // Delete existing splits first
        var existingSplits = await _context.Transactions
            .Where(t => t.Id.StartsWith(transaction.Id + "-split-"))
            .ToListAsync();
        _context.Transactions.RemoveRange(existingSplits);

        transaction.Date = putDate;
        transaction.Description = dto.Description;
        transaction.Category = dto.Category;
        transaction.LedgerCategory = dto.LedgerCategory;
        transaction.Amount = Math.Round(ObfuscationHelper.Deobfuscate(dto.Amount), 2, MidpointRounding.AwayFromZero);
        // Ledger edits (e.g. LedgerView) don't round-trip this field, so only overwrite it
        // when the caller explicitly sends one -- otherwise a manual edit would silently
        // sever the transaction's link back to its originating recurring payment.
        transaction.RecurringPaymentId = dto.RecurringPaymentId ?? transaction.RecurringPaymentId;

        var splitSpec = await ResolveIncomeSplitSpecAsync(transaction);
        AddIncomeSplitTransactions(transaction, splitSpec);

        await SaveAndInvalidateCycleBalancesAsync(originalDate < putDate ? originalDate : putDate);
        return NoContent();
    }

    // DELETE: api/transactions/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteTransaction(string id)
    {
        var transaction = await _context.Transactions.FindAsync(id);
        if (transaction == null)
        {
            return NotFound();
        }

        var splits = await _context.Transactions
            .Where(t => t.Id.StartsWith(id + "-split-"))
            .ToListAsync();
        _context.Transactions.RemoveRange(splits);

        _context.Transactions.Remove(transaction);
        await SaveAndInvalidateCycleBalancesAsync(transaction.Date);

        return NoContent();
    }

    // Resolves the Essentials/Growth/Stability/Rewards split percentages for an income
    // transaction, using the current allocation settings for a fresh "Income" entry or the
    // embedded spec for an "IncomeSplit:" entry (which also normalizes the category back to "Income").
    private async Task<string> ResolveIncomeSplitSpecAsync(Transaction transaction)
    {
        if (string.Equals(transaction.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase))
        {
            var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
            if (setting != null)
            {
                return $"{setting.EssentialsAlloc * 100:0.##},{setting.GrowthAlloc * 100:0.##},{setting.StabilityAlloc * 100:0.##},{setting.RewardsAlloc * 100:0.##}";
            }
            return "";
        }

        if (!string.IsNullOrEmpty(transaction.LedgerCategory) && transaction.LedgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase))
        {
            var spec = transaction.LedgerCategory.Substring("IncomeSplit:".Length);
            transaction.LedgerCategory = "Income";
            return spec;
        }

        return "";
    }

    private void AddIncomeSplitTransactions(Transaction transaction, string splitSpec)
    {
        if (string.IsNullOrEmpty(splitSpec)) return;

        var parts = splitSpec.Split(',');
        if (parts.Length != 4) return;

        var categories = FinancialConstants.BudgetCategories;
        for (int i = 0; i < 4; i++)
        {
            if (decimal.TryParse(parts[i], out var pct) && pct > 0)
            {
                var splitAmount = Math.Round(transaction.Amount * (pct / 100m), 2, MidpointRounding.AwayFromZero);
                _context.Transactions.Add(new Transaction
                {
                    Id = $"{transaction.Id}-split-{categories[i]}",
                    Date = transaction.Date,
                    Description = $"[Split: {categories[i]}] {transaction.Description}",
                    Category = "Transfer",
                    LedgerCategory = $"Transfer:Income->{categories[i]}",
                    Amount = splitAmount
                });
            }
        }
    }

    private static bool TryParseDate(string? value, out DateOnly date)
    {
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static TransactionDto MapToDto(Transaction t)
    {
        return new TransactionDto
        {
            Id = t.Id,
            Date = t.Date.ToString("yyyy-MM-dd"),
            Description = t.Description,
            Category = t.Category,
            LedgerCategory = t.LedgerCategory,
            Amount = ObfuscationHelper.Obfuscate(t.Amount),
            RecurringPaymentId = t.RecurringPaymentId
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
            query = query.Where(t => t.Date >= startDateOnly);
        }
        if (TryParseDate(endDate, out var endDateOnly))
        {
            query = query.Where(t => t.Date <= endDateOnly);
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
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string LedgerCategory { get; set; } = string.Empty;
    public string Amount { get; set; } = string.Empty;
    public string? RecurringPaymentId { get; set; }
}
