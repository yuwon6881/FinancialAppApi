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
    public async Task<ActionResult<object>> PostTransaction(Transaction transaction)
    {
        if (string.IsNullOrWhiteSpace(transaction.Id))
        {
            transaction.Id = $"tx-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        }

        if (string.Equals(transaction.LedgerCategory, "Income", StringComparison.OrdinalIgnoreCase))
        {
            if (transaction.Amount <= 0)
            {
                return BadRequest(new { message = "Income transactions must have a positive amount." });
            }

            var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
            if (setting == null)
            {
                var now = DateTime.Now;
                setting = new FinancialSetting
                {
                    MonthlyIncome = 4000.00m,
                    TargetStabilityFund = 10000.00m,
                    SelectedMonth = now.ToString("MMM"),
                    SelectedYear = now.Year,
                    EssentialsAlloc = 0.50m,
                    GrowthAlloc = 0.25m,
                    StabilityAlloc = 0.15m,
                    RewardsAlloc = 0.10m,
                    CycleDay = 28
                };
                _context.FinancialSettings.Add(setting);
                await _context.SaveChangesAsync();
            }

            var incomeSplitReferenceId = transaction.Id;
            var splitAllocations = FinancialController.GetIncomeSplitAllocations(transaction.Amount, setting);
            var splitTransactions = splitAllocations.Select(split => new Transaction
            {
                Id = $"{incomeSplitReferenceId}-split-{split.LedgerCategory.ToLowerInvariant()}",
                Date = transaction.Date,
                Description = transaction.Description,
                Category = transaction.Category,
                LedgerCategory = split.LedgerCategory,
                Amount = split.Amount,
                IsSplitFromIncome = true
            }).ToList();

            _context.Transactions.AddRange(splitTransactions);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetTransactions), new { id = incomeSplitReferenceId }, splitTransactions);
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
