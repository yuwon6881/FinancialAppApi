using System.Text.Json;
using FinancialAppApi.Database;
using Microsoft.AspNetCore.Mvc;
using FinancialAppApi.Models;
using FinancialAppApi.Filters;
using FinancialAppApi.Services;
using FinancialAppApi.Contracts;

namespace FinancialAppApi.Controllers;

[ApiController]
[Route("api/wishlist")]
[AuthorizeToken]
[RefreshSlices(RefreshSliceNames.Core, RefreshSliceNames.Wishlist)]
public class WishlistController : ControllerBase
{
    private readonly WishlistService _wishlistService;

    public WishlistController(WishlistService wishlistService)
    {
        _wishlistService = wishlistService;
    }

    // GET: api/wishlist
    [HttpGet]
    public async Task<ActionResult<IEnumerable<WishlistItemDto>>> GetWishlist()
    {
        var items = await _wishlistService.GetWishlistAsync(HttpContext.RequestAborted);
        return Ok(items.Select(MapToDto).ToList());
    }

    // POST: api/wishlist
    [HttpPost]
    [RefreshSlices(RefreshSliceNames.Wishlist)]
    public async Task<ActionResult<WishlistItemDto>> PostWishlistItem(WishlistItemMutationDto dto)
    {
        var item = ToWishlistItem(dto);
        var result = await _wishlistService.CreateWishlistItemAsync(item, HttpContext.RequestAborted);
        if (result.Status == WishlistMutationStatus.NameRequired ||
            result.Status == WishlistMutationStatus.PriceInvalid)
        {
            return BadRequest(new { message = result.Message });
        }
        if (result.Status == WishlistMutationStatus.Conflict)
        {
            return Conflict(new { message = result.Message });
        }

        return CreatedAtAction(nameof(GetWishlist), new { id = result.Item!.Id }, MapToDto(result.Item));
    }

    // PUT: api/wishlist/{id}
    [HttpPut("{id}")]
    [RefreshSlices(RefreshSliceNames.Wishlist)]
    public async Task<IActionResult> PutWishlistItem(int id, WishlistItemMutationDto dto)
    {
        var updatedItem = ToWishlistItem(dto);
        var result = await _wishlistService.UpdateWishlistItemAsync(id, updatedItem, HttpContext.RequestAborted);
        return result.Status switch
        {
            WishlistMutationStatus.IdMismatch => BadRequest(new { message = result.Message }),
            WishlistMutationStatus.NotFound => NotFound(),
            WishlistMutationStatus.AlreadyPurchased or WishlistMutationStatus.Conflict => Conflict(new { message = result.Message }),
            WishlistMutationStatus.NameRequired or WishlistMutationStatus.PriceInvalid => BadRequest(new { message = result.Message }),
            _ => Ok(MapToDto(result.Item!))
        };
    }

    // DELETE: api/wishlist/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteWishlistItem(int id)
    {
        var result = await _wishlistService.DeleteWishlistItemAsync(id, HttpContext.RequestAborted);
        if (result == WishlistMutationStatus.NotFound)
        {
            return NotFound();
        }

        return NoContent();
    }

    // POST: api/wishlist/{id}/purchase
    [HttpPost("{id}/purchase")]
    public async Task<IActionResult> PurchaseWishlistItem(int id, [FromBody] PurchaseWishlistRequestDto? dto = null)
    {
        DateTime? customDate = null;
        if (!string.IsNullOrWhiteSpace(dto?.Date))
        {
            if (!TransactionDate.TryParseInputDate(dto.Date, out var parsedDate))
            {
                return BadRequest(new { message = "Date must be a valid calendar date." });
            }
            customDate = TransactionDate.FromInputDate(parsedDate);
        }

        var result = await _wishlistService.PurchaseWishlistItemAsync(
            id,
            customDate,
            HttpContext.RequestAborted,
            dto?.TransactionId,
            dto?.PostedAt,
            dto?.AccountId);
        return result.Status switch
        {
            WishlistMutationStatus.NotFound => NotFound(),
            WishlistMutationStatus.AlreadyPurchased
                or WishlistMutationStatus.PriceInvalid
                or WishlistMutationStatus.DateInvalid
                or WishlistMutationStatus.InvalidAccount => BadRequest(new { code = result.Code, message = result.Message, missingBuckets = result.MissingBuckets }),
            _ => Ok(new { item = MapToDto(result.Item!), transaction = TransactionsController.MapToDto(result.Transaction!) })
        };
    }

    // DELETE: api/wishlist/{id}/purchase
    [HttpDelete("{id}/purchase")]
    public async Task<IActionResult> UnpurchaseWishlistItem(int id)
    {
        var result = await _wishlistService.UnpurchaseWishlistItemAsync(id, HttpContext.RequestAborted);
        if (result.Status == WishlistMutationStatus.NotFound)
        {
            return NotFound();
        }

        var dto = MapToDto(result.Item!);
        dto.UndoTransaction = result.Transaction == null
            ? null
            : TransactionsController.MapToDto(result.Transaction);
        return Ok(dto);
    }

    private static WishlistItem ToWishlistItem(WishlistItemMutationDto dto)
    {
        return new WishlistItem
        {
            Id = dto.Id,
            Name = dto.Name,
            Price = Math.Round(ReadWireAmount(dto.Price), 2, MidpointRounding.AwayFromZero),
            Priority = dto.Priority,
            IsPurchased = dto.IsPurchased,
            PurchasedAt = dto.PurchasedAt,
            PurchaseTransactionId = dto.PurchaseTransactionId,
            CreatedAt = dto.CreatedAt == default ? DateTime.UtcNow : dto.CreatedAt,
            IsActive = dto.IsActive,
            ClientKey = dto.ClientKey
        };
    }

    internal static WishlistItemDto MapToDto(WishlistItem item)
    {
        return new WishlistItemDto
        {
            Id = item.Id,
            Name = item.Name,
            Price = ObfuscationHelper.Obfuscate(item.Price),
            Priority = item.Priority,
            IsPurchased = item.IsPurchased,
            PurchasedAt = item.PurchasedAt,
            PurchaseTransactionId = item.PurchaseTransactionId,
            CreatedAt = item.CreatedAt,
            IsActive = item.IsActive
        };
    }

    internal static WishlistItemDto MapToDto(WishlistItemProjection item)
    {
        return new WishlistItemDto
        {
            Id = item.Id,
            Name = item.Name,
            Price = ObfuscationHelper.Obfuscate(item.Price),
            Priority = item.Priority,
            IsPurchased = item.IsPurchased,
            PurchasedAt = item.PurchasedAt,
            PurchaseTransactionId = item.PurchaseTransactionId,
            CreatedAt = item.CreatedAt,
            IsActive = item.IsActive
        };
    }

    private static decimal ReadWireAmount(JsonElement price)
    {
        return price.ValueKind switch
        {
            JsonValueKind.String => ObfuscationHelper.Deobfuscate(price.GetString() ?? string.Empty),
            JsonValueKind.Number when price.TryGetDecimal(out var value) => value,
            _ => 0m
        };
    }
}

public class WishlistItemMutationDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public JsonElement Price { get; set; }
    public string Priority { get; set; } = "Medium";
    public bool IsPurchased { get; set; }
    public DateTime? PurchasedAt { get; set; }
    public string? PurchaseTransactionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; }
    public string? ClientKey { get; set; }
}

public class WishlistItemDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Price { get; set; } = string.Empty;
    public string Priority { get; set; } = "Medium";
    public bool IsPurchased { get; set; }
    public DateTime? PurchasedAt { get; set; }
    public string? PurchaseTransactionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
    public TransactionDto? UndoTransaction { get; set; }
}

public class PurchaseWishlistRequestDto
{
    public string? Date { get; set; }
    public string? TransactionId { get; set; }
    public DateTime? PostedAt { get; set; }
    public string? AccountId { get; set; }
}
