using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FinancialAppApi.Tests.Integration;

public sealed class BootstrapRefreshIntegrationTests : IntegrationTestBase
{
    [Fact]
    public async Task RefreshRejectsMissingUnknownAndDuplicateSlices()
    {
        var client = await CreateSignedInClientAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/bootstrap/refresh")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/bootstrap/refresh?slices=core,core")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/bootstrap/refresh?slices=unknown")).StatusCode);
    }

    [Fact]
    public async Task RefreshReturnsOnlyRequestedSlicesAndDoesNotPersistPeriod()
    {
        var client = await CreateSignedInClientAsync();
        var before = await client.GetFromJsonAsync<JsonElement>("/api/financial/dashboard?persistSelection=false");
        var beforeMonth = before.GetProperty("setting").GetProperty("selectedMonth").GetString();
        var beforeYear = before.GetProperty("setting").GetProperty("selectedYear").GetInt32();

        var response = await client.GetAsync("/api/bootstrap/refresh?month=Jul&year=2026&slices=categories");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Jul", payload.GetProperty("month").GetString());
        Assert.Equal(2026, payload.GetProperty("year").GetInt32());
        Assert.True(payload.TryGetProperty("categories", out _));
        Assert.False(payload.TryGetProperty("dashboard", out _));
        Assert.False(payload.TryGetProperty("transactions", out _));
        Assert.False(payload.TryGetProperty("wishlist", out _));

        var dashboard = await client.GetFromJsonAsync<JsonElement>("/api/financial/dashboard?persistSelection=false");
        Assert.Equal(beforeMonth, dashboard.GetProperty("setting").GetProperty("selectedMonth").GetString());
        Assert.Equal(beforeYear, dashboard.GetProperty("setting").GetProperty("selectedYear").GetInt32());
    }

    [Fact]
    public async Task CoreRefreshMatchesOverlappingFullBootstrapValues()
    {
        var client = await CreateSignedInClientAsync();

        var partial = await client.GetFromJsonAsync<JsonElement>(
            "/api/bootstrap/refresh?month=Jul&year=2026&slices=core");
        var full = await client.GetFromJsonAsync<JsonElement>(
            "/api/bootstrap?month=Jul&year=2026&persistSelection=false");

        Assert.Equal(full.GetProperty("dashboard").GetRawText(), partial.GetProperty("dashboard").GetRawText());
        Assert.Equal(full.GetProperty("insights").GetRawText(), partial.GetProperty("insights").GetRawText());
        Assert.Equal(full.GetProperty("transactions").GetRawText(), partial.GetProperty("transactions").GetRawText());
        Assert.Equal(full.GetProperty("walletBalance").GetRawText(), partial.GetProperty("walletBalance").GetRawText());
        Assert.Equal(full.GetProperty("accounts").GetRawText(), partial.GetProperty("accounts").GetRawText());
        Assert.Equal(full.GetProperty("autocomplete").GetRawText(), partial.GetProperty("autocomplete").GetRawText());
    }

    [Fact]
    public async Task InvestmentAndDocumentRefreshReturnsOnlyTheirTypedReadModels()
    {
        var client = await CreateSignedInClientAsync();

        var payload = await client.GetFromJsonAsync<JsonElement>(
            "/api/bootstrap/refresh?month=Jul&year=2026&slices=investments,documents");

        var investments = payload.GetProperty("investments");
        Assert.True(investments.TryGetProperty("allocation", out var allocation));
        Assert.True(allocation.TryGetProperty("status", out _));
        Assert.True(allocation.TryGetProperty("freshness", out _));

        var documents = payload.GetProperty("documents");
        Assert.True(documents.TryGetProperty("usage", out var usage));
        Assert.True(usage.TryGetProperty("quotaBytes", out _));
        Assert.True(documents.TryGetProperty("availableYears", out _));
        Assert.True(documents.TryGetProperty("retention", out _));

        Assert.False(payload.TryGetProperty("dashboard", out _));
        Assert.False(payload.TryGetProperty("transactions", out _));
        Assert.False(payload.TryGetProperty("loans", out _));
    }
}
