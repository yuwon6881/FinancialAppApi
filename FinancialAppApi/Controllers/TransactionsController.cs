using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/transactions")]
[AuthorizeToken]
public class TransactionsController : ControllerBase
{
    private readonly AppDbContext _context;

    public TransactionsController(AppDbContext context)
    {
        _context = context;
    }

    // GET: api/transactions
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TransactionDto>>> GetTransactions(
        [FromQuery(Name = "month")] string? queryMonth = null, 
        [FromQuery(Name = "year")] int? queryYear = null,
        [FromQuery(Name = "all")] bool all = false)
    {
        if (all)
        {
            var txs = await _context.Transactions
                .OrderByDescending(t => t.Date)
                .ThenByDescending(t => t.Id)
                .ToListAsync();
            return Ok(txs.Select(MapToDto).ToList());
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

        var months = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
        var activeMonthIndex = Array.IndexOf(months, activeMonth) + 1;
        if (activeMonthIndex == 0) activeMonthIndex = 6;

        var (cycleStart, cycleEnd, _) = FinancialController.GetCycleRange(activeYear, activeMonthIndex, setting.CycleDay);

        var allTransactions = await _context.Transactions.ToListAsync();

        var filtered = allTransactions.Where(t =>
        {
            if (DateTime.TryParse(t.Date, out var date))
            {
                return date >= cycleStart && date <= cycleEnd;
            }
            return false;
        })
        .OrderByDescending(t => t.Date)
        .ThenByDescending(t => t.Id)
        .Select(MapToDto)
        .ToList();

        return Ok(filtered);
    }

    // POST: api/transactions
    [HttpPost]
    public async Task<ActionResult<TransactionDto>> PostTransaction(TransactionDto dto)
    {
        var transaction = new Transaction
        {
            Id = string.IsNullOrWhiteSpace(dto.Id) ? $"tx-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" : dto.Id,
            Date = dto.Date,
            Description = dto.Description,
            Category = dto.Category,
            LedgerCategory = dto.LedgerCategory,
            Amount = ObfuscationHelper.Deobfuscate(dto.Amount)
        };

        string splitSpec = "";
        if (string.Equals(transaction.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase))
        {
            var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
            if (setting != null)
            {
                splitSpec = $"{setting.EssentialsAlloc * 100:0.##},{setting.GrowthAlloc * 100:0.##},{setting.StabilityAlloc * 100:0.##},{setting.RewardsAlloc * 100:0.##}";
            }
        }
        else if (!string.IsNullOrEmpty(transaction.LedgerCategory) && transaction.LedgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase))
        {
            splitSpec = transaction.LedgerCategory.Substring("IncomeSplit:".Length);
            transaction.LedgerCategory = "Income";
        }

        _context.Transactions.Add(transaction);

        if (!string.IsNullOrEmpty(splitSpec))
        {
            var parts = splitSpec.Split(',');
            if (parts.Length == 4)
            {
                var categories = new[] { "Essentials", "Growth", "Stability", "Rewards" };
                for (int i = 0; i < 4; i++)
                {
                    if (decimal.TryParse(parts[i], out var pct) && pct > 0)
                    {
                        var splitAmount = transaction.Amount * (pct / 100m);
                        var splitTx = new Transaction
                        {
                            Id = $"{transaction.Id}-split-{categories[i]}",
                            Date = transaction.Date,
                            Description = $"[Split: {categories[i]}] {transaction.Description}",
                            Category = "Other",
                            LedgerCategory = $"Transfer:Income->{categories[i]}",
                            Amount = splitAmount
                        };
                        _context.Transactions.Add(splitTx);
                    }
                }
            }
        }

        await _context.SaveChangesAsync();

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

        // Delete existing splits first
        var existingSplits = await _context.Transactions
            .Where(t => t.Id.StartsWith(transaction.Id + "-split-"))
            .ToListAsync();
        _context.Transactions.RemoveRange(existingSplits);

        transaction.Date = dto.Date;
        transaction.Description = dto.Description;
        transaction.Category = dto.Category;
        transaction.LedgerCategory = dto.LedgerCategory;
        transaction.Amount = ObfuscationHelper.Deobfuscate(dto.Amount);

        string splitSpec = "";
        if (string.Equals(transaction.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase))
        {
            var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
            if (setting != null)
            {
                splitSpec = $"{setting.EssentialsAlloc * 100:0.##},{setting.GrowthAlloc * 100:0.##},{setting.StabilityAlloc * 100:0.##},{setting.RewardsAlloc * 100:0.##}";
            }
        }
        else if (!string.IsNullOrEmpty(transaction.LedgerCategory) && transaction.LedgerCategory.StartsWith("IncomeSplit:", StringComparison.OrdinalIgnoreCase))
        {
            splitSpec = transaction.LedgerCategory.Substring("IncomeSplit:".Length);
            transaction.LedgerCategory = "Income";
        }

        if (!string.IsNullOrEmpty(splitSpec))
        {
            var parts = splitSpec.Split(',');
            if (parts.Length == 4)
            {
                var categories = new[] { "Essentials", "Growth", "Stability", "Rewards" };
                for (int i = 0; i < 4; i++)
                {
                    if (decimal.TryParse(parts[i], out var pct) && pct > 0)
                    {
                        var splitAmount = transaction.Amount * (pct / 100m);
                        var splitTx = new Transaction
                        {
                            Id = $"{transaction.Id}-split-{categories[i]}",
                            Date = transaction.Date,
                            Description = $"[Split: {categories[i]}] {transaction.Description}",
                            Category = "Other",
                            LedgerCategory = $"Transfer:Income->{categories[i]}",
                            Amount = splitAmount
                        };
                        _context.Transactions.Add(splitTx);
                    }
                }
            }
        }

        await _context.SaveChangesAsync();
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
        await _context.SaveChangesAsync();

        return NoContent();
    }

    private static TransactionDto MapToDto(Transaction t)
    {
        return new TransactionDto
        {
            Id = t.Id,
            Date = t.Date,
            Description = t.Description,
            Category = t.Category,
            LedgerCategory = t.LedgerCategory,
            Amount = ObfuscationHelper.Obfuscate(t.Amount)
        };
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
}
