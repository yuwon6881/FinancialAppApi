using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinancialAppApi.Database;

namespace FinancialAppApi.Tests.Integration;

/// <summary>
/// Transaction create/read workflow through the full HTTP stack, including the obfuscated-amount
/// wire contract and client-id idempotency.
/// </summary>
public class TransactionsIntegrationTests : IntegrationTestBase
{
    [Fact]
    public async Task PostTransaction_CreatesAndAppearsInList()
    {
        var client = await CreateSignedInClientAsync();

        var dto = new
        {
            date = "2026-06-15",
            description = "Groceries",
            category = "Food",
            ledgerCategory = "Essentials",
            amount = ObfuscationHelper.Obfuscate(-42.50m),
        };

        var create = await client.PostAsJsonAsync("/api/transactions", dto);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Groceries", created.GetProperty("description").GetString());
        Assert.Equal(-42.50m, ObfuscationHelper.Deobfuscate(created.GetProperty("amount").GetString()!));

        // The transaction is retrievable via the (all=true) paginated listing.
        var list = await client.GetFromJsonAsync<JsonElement>("/api/transactions?all=true");
        var items = list.GetProperty("items");
        Assert.Contains(items.EnumerateArray(),
            t => t.GetProperty("description").GetString() == "Groceries");
    }

    [Fact]
    public async Task PostTransaction_WithUnknownCategory_Returns400()
    {
        var client = await CreateSignedInClientAsync();

        var dto = new
        {
            date = "2026-06-15",
            description = "Mystery",
            category = "NoSuchCategory",
            ledgerCategory = "Essentials",
            amount = ObfuscationHelper.Obfuscate(-10m),
        };

        var create = await client.PostAsJsonAsync("/api/transactions", dto);

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task PostTransaction_WithBadDate_Returns400()
    {
        var client = await CreateSignedInClientAsync();

        var dto = new
        {
            date = "06/15/2026",
            description = "Bad date",
            category = "Food",
            ledgerCategory = "Essentials",
            amount = ObfuscationHelper.Obfuscate(-10m),
        };

        var create = await client.PostAsJsonAsync("/api/transactions", dto);

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task PostTransaction_WithSameClientId_IsIdempotent()
    {
        var client = await CreateSignedInClientAsync();
        var id = "tx-client-supplied-1";
        var dto = new
        {
            id,
            date = "2026-06-15",
            description = "Coffee",
            category = "Food",
            ledgerCategory = "Essentials",
            amount = ObfuscationHelper.Obfuscate(-4m),
        };

        var first = await client.PostAsJsonAsync("/api/transactions", dto);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Re-POSTing the same id dedupes: 200 (not 201) and no duplicate row.
        var second = await client.PostAsJsonAsync("/api/transactions", dto);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/transactions?all=true");
        var matches = list.GetProperty("items").EnumerateArray()
            .Count(t => t.GetProperty("id").GetString() == id);
        Assert.Equal(1, matches);
    }

    [Fact]
    public async Task GetTransaction_UnknownId_Returns404()
    {
        var client = await CreateSignedInClientAsync();

        var response = await client.GetAsync("/api/transactions/tx-does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteTransaction_RemovesIt()
    {
        var client = await CreateSignedInClientAsync();
        var dto = new
        {
            id = "tx-to-delete",
            date = "2026-06-15",
            description = "Temp",
            category = "Food",
            ledgerCategory = "Essentials",
            amount = ObfuscationHelper.Obfuscate(-1m),
        };
        await client.PostAsJsonAsync("/api/transactions", dto);

        var delete = await client.DeleteAsync("/api/transactions/tx-to-delete");
        Assert.True(delete.IsSuccessStatusCode);

        var get = await client.GetAsync("/api/transactions/tx-to-delete");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }
}
