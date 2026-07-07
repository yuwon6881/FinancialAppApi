using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;
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

        public WishlistController(AppDbContext context)
        {
            _context = context;
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
                Date = DateOnly.FromDateTime(DateTime.Now), // Local date matches other postings
                Description = $"Purchased: {item.Name} (Wish List)",
                Category = "Other", // Main category matches ledger sub-categorizations
                LedgerCategory = "Rewards", // The category that funds this purchase
                Amount = -item.Price // Debit amount
            };

            _context.Transactions.Add(tx);
            await _context.SaveChangesAsync();

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
    }
}
