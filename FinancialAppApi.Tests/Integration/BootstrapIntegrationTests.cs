using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinancialAppApi.Database;

namespace FinancialAppApi.Tests.Integration;

/// <summary>
/// /api/bootstrap replaces the eight GETs a cold launch used to make, so its job is to return
/// exactly what those endpoints return. These tests compare the two slice by slice — if the
/// composite payload ever drifts from the individual endpoints, the app would silently boot
/// with different data than a refresh produces.
/// </summary>
public class BootstrapIntegrationTests : IntegrationTestBase
{
    private static async Task PostTransactionAsync(
        HttpClient client, string date, string category, string ledgerCategory, decimal amount)
    {
        var response = await client.PostAsJsonAsync("/api/transactions", new
        {
            date,
            description = $"seed-{date}-{category}",
            category,
            ledgerCategory,
            // A plain bucket row names its own account; an Income parent carries none and instead
            // places each generated child, since placement is explicit and has no default.
            accountId = LedgerBuckets.Contains(ledgerCategory) ? AccountIdFor(ledgerCategory) : null,
            splitAccountIds = LedgerBuckets.Contains(ledgerCategory)
                ? null
                : LedgerBuckets.ToDictionary(bucket => bucket, bucket => AccountIdFor(bucket)),
            amount = ObfuscationHelper.Obfuscate(amount),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_ReturnsEverySliceTheAppNeedsToRender()
    {
        var client = await CreateSignedInClientAsync();

        var bootstrap = await client.GetFromJsonAsync<JsonElement>("/api/bootstrap");

        foreach (var slice in new[]
        {
            "month", "year", "dashboard", "insights", "transactions", "recurringPayments",
            "categories", "wishlist", "autocomplete", "walletBalance",
        })
        {
            Assert.True(bootstrap.TryGetProperty(slice, out _), $"missing slice: {slice}");
        }
    }

    [Fact]
    public async Task Bootstrap_MatchesTheIndividualEndpointsItReplaces()
    {
        var client = await CreateSignedInClientAsync();
        await PostTransactionAsync(client, "2026-07-09", "Food", "Essentials", -100m);
        await PostTransactionAsync(client, "2026-07-10", "Salary", "Income", 3000m);

        var bootstrap = await client.GetFromJsonAsync<JsonElement>("/api/bootstrap");
        var month = bootstrap.GetProperty("month").GetString();
        var year = bootstrap.GetProperty("year").GetInt32();

        // Fetch the individual endpoints for the same period bootstrap resolved, so any
        // difference is a composition bug rather than the two looking at different cycles.
        var dashboard = await client.GetFromJsonAsync<JsonElement>(
            $"/api/financial/dashboard?month={month}&year={year}");
        var insights = await client.GetFromJsonAsync<JsonElement>(
            $"/api/financial/dashboard/insights?month={month}&year={year}");
        var transactions = await client.GetFromJsonAsync<JsonElement>(
            $"/api/transactions?month={month}&year={year}");
        var recurring = await client.GetFromJsonAsync<JsonElement>("/api/recurring-payments");
        var categories = await client.GetFromJsonAsync<JsonElement>("/api/categories");
        var wishlist = await client.GetFromJsonAsync<JsonElement>("/api/wishlist");
        var autocomplete = await client.GetFromJsonAsync<JsonElement>("/api/transactions/autocomplete");
        var wallet = await client.GetFromJsonAsync<JsonElement>("/api/financial/wallet-balance");

        Assert.Equal(Json(dashboard), Json(bootstrap.GetProperty("dashboard")));
        Assert.Equal(Json(insights), Json(bootstrap.GetProperty("insights")));
        Assert.Equal(Json(transactions), Json(bootstrap.GetProperty("transactions")));
        Assert.Equal(Json(recurring), Json(bootstrap.GetProperty("recurringPayments")));
        Assert.Equal(Json(categories), Json(bootstrap.GetProperty("categories")));
        Assert.Equal(Json(wishlist), Json(bootstrap.GetProperty("wishlist")));
        Assert.Equal(Json(autocomplete), Json(bootstrap.GetProperty("autocomplete")));
        Assert.Equal(Json(wallet), Json(bootstrap.GetProperty("walletBalance")));
    }

    [Fact]
    public async Task Bootstrap_HonorsAnExplicitPeriod()
    {
        var client = await CreateSignedInClientAsync();

        var bootstrap = await client.GetFromJsonAsync<JsonElement>("/api/bootstrap?month=Mar&year=2025");

        Assert.Equal("Mar", bootstrap.GetProperty("month").GetString());
        Assert.Equal(2025, bootstrap.GetProperty("year").GetInt32());
        Assert.Equal(
            "Mar",
            bootstrap.GetProperty("dashboard").GetProperty("setting").GetProperty("selectedMonth").GetString());
    }

    [Fact]
    public async Task Bootstrap_PersistsTheSelectedPeriodLikeTheDashboardDoes()
    {
        var client = await CreateSignedInClientAsync();

        await client.GetFromJsonAsync<JsonElement>("/api/bootstrap?month=Feb&year=2025");

        // A later request with no period must see the persisted selection reflected in settings.
        var dashboard = await client.GetFromJsonAsync<JsonElement>(
            "/api/financial/dashboard?month=Feb&year=2025&persistSelection=false");
        Assert.Equal("Feb", dashboard.GetProperty("setting").GetProperty("selectedMonth").GetString());
        Assert.Equal(2025, dashboard.GetProperty("setting").GetProperty("selectedYear").GetInt32());
    }

    [Fact]
    public async Task Bootstrap_RejectsAMalformedPeriod()
    {
        var client = await CreateSignedInClientAsync();

        var response = await client.GetAsync("/api/bootstrap?month=NotAMonth&year=2026");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_RequiresAuthentication()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/api/bootstrap");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string Json(JsonElement element) => element.GetRawText();
}
