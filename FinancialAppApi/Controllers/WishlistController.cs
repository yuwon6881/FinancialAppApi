using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;
using FinancialAppApi.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FinancialAppApi.Controllers
{
    [ApiController]
    [Route("api/wishlist")]
    [AuthorizeToken]
    public class WishlistController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly CycleBalanceService _cycleBalanceService;

        public WishlistController(AppDbContext context, CycleBalanceService cycleBalanceService)
        {
            _context = context;
            _cycleBalanceService = cycleBalanceService;
        }

        // GET: api/wishlist
        [HttpGet]
        public async Task<ActionResult<IEnumerable<WishlistItem>>> GetWishlist()
        {
            return await _context.WishlistItems
                .OrderByDescending(w => w.IsActive)
                .ThenByDescending(w => w.CreatedAt)
                .ToListAsync();
        }

        // POST: api/wishlist
        [HttpPost]
        public async Task<ActionResult<WishlistItem>> PostWishlistItem(WishlistItem item)
        {
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                return BadRequest(new { message = "Item name is required." });
            }
            if (item.Price <= 0)
            {
                return BadRequest(new { message = "Item price must be greater than zero." });
            }

            item.CreatedAt = DateTime.UtcNow;
            item.IsPurchased = false;
            item.PurchasedAt = null;
            item.PurchaseTransactionId = null;

            // If we mark it active, deactivate other items
            if (item.IsActive)
            {
                var activeItems = await _context.WishlistItems.Where(w => w.IsActive).ToListAsync();
                foreach (var activeItem in activeItems)
                {
                    activeItem.IsActive = false;
                }
            }
            else
            {
                // If there are no items in the wishlist at all, make this new item the active one by default
                var hasAny = await _context.WishlistItems.AnyAsync();
                if (!hasAny)
                {
                    item.IsActive = true;
                }
            }

            _context.WishlistItems.Add(item);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetWishlist), new { id = item.Id }, item);
        }

        // PUT: api/wishlist/{id}
        [HttpPut("{id}")]
        public async Task<IActionResult> PutWishlistItem(int id, WishlistItem updatedItem)
        {
            if (id != updatedItem.Id)
            {
                return BadRequest(new { message = "ID mismatch." });
            }

            var item = await _context.WishlistItems.FindAsync(id);
            if (item == null)
            {
                return NotFound();
            }

            if (string.IsNullOrWhiteSpace(updatedItem.Name))
            {
                return BadRequest(new { message = "Item name is required." });
            }
            if (updatedItem.Price <= 0)
            {
                return BadRequest(new { message = "Item price must be greater than zero." });
            }

            item.Name = updatedItem.Name;
            item.Price = updatedItem.Price;
            item.Priority = updatedItem.Priority;

            if (updatedItem.IsActive && !item.IsActive)
            {
                var activeItems = await _context.WishlistItems.Where(w => w.IsActive && w.Id != id).ToListAsync();
                foreach (var activeItem in activeItems)
                {
                    activeItem.IsActive = false;
                }
                item.IsActive = true;
            }
            else if (!updatedItem.IsActive && item.IsActive)
            {
                item.IsActive = false;
            }

            await _context.SaveChangesAsync();
            return NoContent();
        }

        // DELETE: api/wishlist/{id}
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteWishlistItem(int id)
        {
            var item = await _context.WishlistItems.FindAsync(id);
            if (item == null)
            {
                return NotFound();
            }

            bool wasActive = item.IsActive;

            _context.WishlistItems.Remove(item);
            await _context.SaveChangesAsync();

            // If we deleted the active item, make the next most recent item active
            if (wasActive)
            {
                var nextItem = await _context.WishlistItems
                    .OrderByDescending(w => w.CreatedAt)
                    .FirstOrDefaultAsync();
                if (nextItem != null)
                {
                    nextItem.IsActive = true;
                    await _context.SaveChangesAsync();
                }
            }

            return NoContent();
        }

        // POST: api/wishlist/{id}/purchase
        [HttpPost("{id}/purchase")]
        public async Task<IActionResult> PurchaseWishlistItem(int id)
        {
            var item = await _context.WishlistItems.FindAsync(id);
            if (item == null)
            {
                return NotFound();
            }
            if (item.IsPurchased)
            {
                return BadRequest(new { message = "Item is already purchased." });
            }

            // Mark as purchased and inactive
            item.IsPurchased = true;
            item.PurchasedAt = DateTime.UtcNow;
            item.IsActive = false;

            // Generate corresponding debit transaction in ledger
            var tx = new Transaction
            {
                Id = Guid.NewGuid().ToString("N"),
                Date = DateTime.UtcNow,
                Description = $"Purchased: {item.Name} (Wish List)",
                Category = "Other", // Main category matches ledger sub-categorizations
                LedgerCategory = "Rewards", // The category that funds this purchase
                Amount = -item.Price, // Debit amount
                WishlistItemId = item.Id
            };

            item.PurchaseTransactionId = tx.Id;
            _context.Transactions.Add(tx);

            // Persist the transaction and invalidate the affected cycle balance cache atomically
            // -- this app can be signed in on multiple devices, and the cache is shared (not
            // per-device), so without this a dashboard read from another device could land in
            // the gap between "transaction saved" and "cache invalidated" and see stale figures.
            // Must go through CreateExecutionStrategy().ExecuteAsync(...) rather than a bare
            // BeginTransactionAsync() -- Npgsql's EnableRetryOnFailure() retrying execution
            // strategy (Program.cs) refuses to run a user-started transaction directly.
            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var dbTransaction = await _context.Database.BeginTransactionAsync();
                await _context.SaveChangesAsync();

                var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
                if (setting != null)
                {
                    var (cycleYear, cycleMonthIndex) = FinancialController.GetCycleYearAndMonthIndexForDate(TransactionDate.ToDateOnly(tx.Date), setting.CycleDay);
                    await _cycleBalanceService.InvalidateFromAsync(cycleYear, cycleMonthIndex);
                }

                await dbTransaction.CommitAsync();
            });

            // Make another item active if possible
            var nextItem = await _context.WishlistItems
                .Where(w => !w.IsPurchased)
                .OrderByDescending(w => w.CreatedAt)
                .FirstOrDefaultAsync();
            if (nextItem != null)
            {
                nextItem.IsActive = true;
                await _context.SaveChangesAsync();
            }

            return Ok(new { item, transaction = tx });
        }

        // DELETE: api/wishlist/{id}/purchase
        [HttpDelete("{id}/purchase")]
        public async Task<IActionResult> UnpurchaseWishlistItem(int id)
        {
            var item = await _context.WishlistItems.FindAsync(id);
            if (item == null)
            {
                return NotFound();
            }

            var purchaseTransactionId = item.PurchaseTransactionId;
            var transaction = !string.IsNullOrWhiteSpace(purchaseTransactionId)
                ? await _context.Transactions.FindAsync(purchaseTransactionId)
                : null;

            DateTime? affectedDate = transaction?.Date;

            item.IsPurchased = false;
            item.PurchasedAt = null;
            item.PurchaseTransactionId = null;

            var hasActiveUnpurchased = await _context.WishlistItems
                .AnyAsync(w => w.Id != item.Id && !w.IsPurchased && w.IsActive);
            if (!hasActiveUnpurchased)
            {
                item.IsActive = true;
            }

            if (transaction != null)
            {
                var splits = await _context.Transactions
                    .Where(t => t.Id.StartsWith(transaction.Id + "-split-"))
                    .ToListAsync();
                _context.Transactions.RemoveRange(splits);
                _context.Transactions.Remove(transaction);
            }

            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var dbTransaction = await _context.Database.BeginTransactionAsync();
                await _context.SaveChangesAsync();

                if (affectedDate.HasValue)
                {
                    var setting = await _context.FinancialSettings.FirstOrDefaultAsync();
                    if (setting != null)
                    {
                        var (cycleYear, cycleMonthIndex) = FinancialController.GetCycleYearAndMonthIndexForDate(TransactionDate.ToDateOnly(affectedDate.Value), setting.CycleDay);
                        await _cycleBalanceService.InvalidateFromAsync(cycleYear, cycleMonthIndex);
                    }
                }

                await dbTransaction.CommitAsync();
            });

            return Ok(item);
        }
    }
}
