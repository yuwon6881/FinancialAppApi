using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Tests.Integration;

/// <summary>
/// Wishlist workflow through the full HTTP stack, focused on the purchase-to-transaction linkage:
/// purchasing an item must create a linked <c>Transaction</c> and mark the item purchased.
/// </summary>
public class WishlistIntegrationTests : IntegrationTestBase
{
    private static async Task<int> CreateItemAsync(HttpClient client, string name, decimal price)
    {
        var create = await client.PostAsJsonAsync("/api/wishlist", new
        {
            name,
            price = ObfuscationHelper.Obfuscate(price),
            priority = "Medium",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task PostWishlistItem_CreatesItem()
    {
        var client = await CreateSignedInClientAsync();

        var id = await CreateItemAsync(client, "New Laptop", 1500m);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/wishlist");
        Assert.Contains(list.EnumerateArray(), i => i.GetProperty("id").GetInt32() == id);
    }

    [Fact]
    public async Task PostWishlistItem_WithEmptyName_Returns400()
    {
        var client = await CreateSignedInClientAsync();

        var create = await client.PostAsJsonAsync("/api/wishlist", new
        {
            name = "",
            price = ObfuscationHelper.Obfuscate(10m),
        });

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task PurchaseWishlistItem_CreatesLinkedTransaction()
    {
        var client = await CreateSignedInClientAsync();
        var id = await CreateItemAsync(client, "Headphones", 200m);

        await Factory.WithDbContextAsync(async db =>
        {
            db.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid().ToString("N"),
                Date = DateTime.UtcNow,
                Amount = 1000m,
                Category = "Other",
                LedgerCategory = "Rewards"
            });
            await db.SaveChangesAsync();
        });

        var purchase = await client.PostAsJsonAsync($"/api/wishlist/{id}/purchase", new
        {
            accountId = AccountIdFor("Rewards"),
        });
        Assert.Equal(HttpStatusCode.OK, purchase.StatusCode);

        var body = await purchase.Content.ReadFromJsonAsync<JsonElement>();
        var item = body.GetProperty("item");
        var transaction = body.GetProperty("transaction");

        // Item is now purchased and inactive.
        Assert.True(item.GetProperty("isPurchased").GetBoolean());

        // A linked transaction was created with a negative amount matching the price.
        Assert.Equal(-200m, ObfuscationHelper.Deobfuscate(transaction.GetProperty("amount").GetString()!));
        Assert.Equal(id, transaction.GetProperty("wishlistItemId").GetInt32());

        // The link is bidirectional: the item points back at the transaction.
        Assert.Equal(
            transaction.GetProperty("id").GetString(),
            item.GetProperty("purchaseTransactionId").GetString());

        // And the transaction is retrievable from the transactions endpoint.
        var txId = transaction.GetProperty("id").GetString();
        var getTx = await client.GetAsync($"/api/transactions/{txId}");
        Assert.Equal(HttpStatusCode.OK, getTx.StatusCode);
    }

    [Fact]
    public async Task PurchaseWishlistItem_Twice_ReturnsSameSuccessfulPurchase()
    {
        var client = await CreateSignedInClientAsync();
        var id = await CreateItemAsync(client, "Monitor", 300m);
        await Factory.WithDbContextAsync(async db =>
        {
            db.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid().ToString("N"),
                Date = DateTime.UtcNow,
                Amount = 1000m,
                Category = "Other",
                LedgerCategory = "Rewards"
            });
            await db.SaveChangesAsync();
        });
        await client.PostAsJsonAsync($"/api/wishlist/{id}/purchase", new
        {
            accountId = AccountIdFor("Rewards"),
        });

        var second = await client.PostAsJsonAsync($"/api/wishlist/{id}/purchase", new
        {
            accountId = AccountIdFor("Rewards"),
        });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var transactions = await client.GetFromJsonAsync<JsonElement>("/api/transactions?all=true&pageSize=500");
        var linked = transactions.GetProperty("items").EnumerateArray()
            .Count(transaction => transaction.TryGetProperty("wishlistItemId", out var itemId) && itemId.GetInt32() == id);
        Assert.Equal(1, linked);
    }

    [Fact]
    public async Task UnpurchaseWishlistItem_ClearsLink()
    {
        var client = await CreateSignedInClientAsync();
        var id = await CreateItemAsync(client, "Keyboard", 120m);
        await Factory.WithDbContextAsync(async db =>
        {
            db.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid().ToString("N"),
                Date = DateTime.UtcNow,
                Amount = 1000m,
                Category = "Other",
                LedgerCategory = "Rewards"
            });
            await db.SaveChangesAsync();
        });
        await client.PostAsJsonAsync($"/api/wishlist/{id}/purchase", new
        {
            accountId = AccountIdFor("Rewards"),
        });

        var unpurchase = await client.DeleteAsync($"/api/wishlist/{id}/purchase");
        Assert.Equal(HttpStatusCode.OK, unpurchase.StatusCode);

        var body = await unpurchase.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isPurchased").GetBoolean());
    }

    [Fact]
    public async Task PurchaseWishlistItem_UnknownId_Returns404()
    {
        var client = await CreateSignedInClientAsync();

        var purchase = await client.PostAsync("/api/wishlist/999999/purchase", null);

        Assert.Equal(HttpStatusCode.NotFound, purchase.StatusCode);
    }
}
