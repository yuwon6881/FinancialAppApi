using Microsoft.AspNetCore.Mvc;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;
using FinancialAppApi.Services;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/wishlist")]
[AuthorizeToken]
public class WishlistController : ControllerBase
{
    private readonly WishlistService _wishlistService;

    public WishlistController(WishlistService wishlistService)
    {
        _wishlistService = wishlistService;
    }

    // GET: api/wishlist
    [HttpGet]
    public async Task<ActionResult<IEnumerable<WishlistItem>>> GetWishlist()
    {
        return await _wishlistService.GetWishlistAsync();
    }

    // POST: api/wishlist
    [HttpPost]
    public async Task<ActionResult<WishlistItem>> PostWishlistItem(WishlistItem item)
    {
        var result = await _wishlistService.CreateWishlistItemAsync(item);
        if (result.Status == WishlistMutationStatus.NameRequired ||
            result.Status == WishlistMutationStatus.PriceInvalid)
        {
            return BadRequest(new { message = result.Message });
        }

        return CreatedAtAction(nameof(GetWishlist), new { id = result.Item!.Id }, result.Item);
    }

    // PUT: api/wishlist/{id}
    [HttpPut("{id}")]
    public async Task<IActionResult> PutWishlistItem(int id, WishlistItem updatedItem)
    {
        var result = await _wishlistService.UpdateWishlistItemAsync(id, updatedItem);
        return result.Status switch
        {
            WishlistMutationStatus.IdMismatch => BadRequest(new { message = result.Message }),
            WishlistMutationStatus.NotFound => NotFound(),
            WishlistMutationStatus.NameRequired or WishlistMutationStatus.PriceInvalid => BadRequest(new { message = result.Message }),
            _ => NoContent()
        };
    }

    // DELETE: api/wishlist/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteWishlistItem(int id)
    {
        var result = await _wishlistService.DeleteWishlistItemAsync(id);
        if (result == WishlistMutationStatus.NotFound)
        {
            return NotFound();
        }

        return NoContent();
    }

    // POST: api/wishlist/{id}/purchase
    [HttpPost("{id}/purchase")]
    public async Task<IActionResult> PurchaseWishlistItem(int id)
    {
        var result = await _wishlistService.PurchaseWishlistItemAsync(id);
        return result.Status switch
        {
            WishlistMutationStatus.NotFound => NotFound(),
            WishlistMutationStatus.AlreadyPurchased => BadRequest(new { message = result.Message }),
            _ => Ok(new { item = result.Item, transaction = result.Transaction })
        };
    }

    // DELETE: api/wishlist/{id}/purchase
    [HttpDelete("{id}/purchase")]
    public async Task<IActionResult> UnpurchaseWishlistItem(int id)
    {
        var result = await _wishlistService.UnpurchaseWishlistItemAsync(id);
        if (result.Status == WishlistMutationStatus.NotFound)
        {
            return NotFound();
        }

        return Ok(result.Item);
    }
}
