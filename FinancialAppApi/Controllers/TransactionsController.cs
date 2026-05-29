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
    public async Task<ActionResult<IEnumerable<Transaction>>> GetTransactions(
        [FromQuery(Name = "month")] string? queryMonth = null, 
        [FromQuery(Name = "year")] int? queryYear = null)
    {
        var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
        if (setting == null)
        {
            return await _context.Transactions
                .OrderByDescending(t => t.Date)
                .ToListAsync();
        }

        string activeMonth = queryMonth ?? setting.SelectedMonth;
        int activeYear = queryYear ?? setting.SelectedYear;

        var months = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
        var activeMonthIndex = Array.IndexOf(months, activeMonth) + 1;
        if (activeMonthIndex == 0) activeMonthIndex = 6;

        var (cycleStart, cycleEnd, _) = FinancialController.GetCycleRange(activeYear, activeMonthIndex, setting.CycleDay);

        var allTransactions = await _context.Transactions.ToListAsync();

        return allTransactions.Where(t =>
        {
            if (DateTime.TryParse(t.Date, out var date))
            {
                return date >= cycleStart && date <= cycleEnd;
            }
            return false;
        })
        .OrderByDescending(t => t.Date)
        .ToList();
    }

    // POST: api/transactions
    [HttpPost]
    public async Task<ActionResult<Transaction>> PostTransaction(Transaction transaction)
    {
        if (string.IsNullOrWhiteSpace(transaction.Id))
        {
            transaction.Id = $"tx-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        }

        if (string.Equals(transaction.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase))
        {
            var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
            if (setting != null)
            {
                transaction.LedgerCategory = $"IncomeSplit:{setting.EssentialsAlloc * 100:0.##},{setting.GrowthAlloc * 100:0.##},{setting.StabilityAlloc * 100:0.##},{setting.RewardsAlloc * 100:0.##}";
            }
        }

        _context.Transactions.Add(transaction);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetTransactions), new { id = transaction.Id }, transaction);
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

        _context.Transactions.Remove(transaction);
        await _context.SaveChangesAsync();

        return NoContent();
    }
}
