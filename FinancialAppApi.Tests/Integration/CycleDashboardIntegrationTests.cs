using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests.Integration;

/// <summary>
/// Cycle-switching and dashboard-recalculation workflow through the full HTTP stack: the dashboard
/// auto-creates settings, reflects new transactions, and honors the selected period.
/// </summary>
public class CycleDashboardIntegrationTests : IntegrationTestBase
{
    private static async Task PostTransactionAsync(
        HttpClient client, string date, string category, string ledgerCategory, decimal amount)
    {
        var response = await client.PostAsJsonAsync("/api/transactions", new
        {
            date,
            description = "seed",
            category,
            ledgerCategory,
            accountId = AccountIdFor(ledgerCategory),
            amount = ObfuscationHelper.Obfuscate(amount),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Dashboard_OnFreshUser_ReturnsSettingsAndStats()
    {
        var client = await CreateSignedInClientAsync();

        var dashboard = await client.GetFromJsonAsync<JsonElement>("/api/financial/dashboard");

        Assert.True(dashboard.TryGetProperty("setting", out _));
        Assert.True(dashboard.TryGetProperty("stats", out var stats));
        Assert.True(stats.TryGetProperty("totalBalance", out _));
        Assert.True(dashboard.TryGetProperty("cycleLabel", out _));
    }

    [Fact]
    public async Task UpdateSettings_PersistsAndReflectsInDashboard()
    {
        var client = await CreateSignedInClientAsync();

        var update = await client.PutAsJsonAsync("/api/financial/settings", new
        {
            targetStabilityFund = ObfuscationHelper.Obfuscate(5000m),
            essentialsAlloc = 0.40m,
            growthAlloc = 0.30m,
            stabilityAlloc = 0.20m,
            rewardsAlloc = 0.10m,
            cycleDay = 15,
            currency = "EUR",
        });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var dashboard = await client.GetFromJsonAsync<JsonElement>("/api/financial/dashboard");
        var setting = dashboard.GetProperty("setting");
        Assert.Equal("EUR", setting.GetProperty("currency").GetString());
        Assert.Equal(15, setting.GetProperty("cycleDay").GetInt32());
        Assert.Equal(5000m,
            ObfuscationHelper.Deobfuscate(setting.GetProperty("targetStabilityFund").GetString()!));
    }

    [Fact]
    public async Task SelectPeriod_SwitchesCycle()
    {
        var client = await CreateSignedInClientAsync();

        var select = await client.PostAsJsonAsync("/api/financial/select-period", new
        {
            targetStabilityFund = 0m,
            selectedMonth = "Mar",
            selectedYear = 2025,
            cycleDay = 28,
            currency = "USD",
            hideSensitive = true,
            vibrationEnabled = true,
        });
        Assert.Equal(HttpStatusCode.NoContent, select.StatusCode);

        // Assert the persisted selection directly: GET /dashboard with no query params is itself the
        // "writer of record" and would reset the selection to the current period, masking this.
        await Factory.WithDbContextAsync(async db =>
        {
            var setting = await db.FinancialSettings.FirstAsync();
            Assert.Equal("Mar", setting.SelectedMonth);
            Assert.Equal(2025, setting.SelectedYear);
        });
    }

    [Fact]
    public async Task Dashboard_RecalculatesAfterNewTransaction()
    {
        var client = await CreateSignedInClientAsync();

        var before = await client.GetFromJsonAsync<JsonElement>("/api/financial/dashboard");
        var balanceBefore = ObfuscationHelper.Deobfuscate(
            before.GetProperty("stats").GetProperty("totalBalance").GetString()!);

        // Record an expense; the dashboard's wallet balance must recompute to include it.
        await PostTransactionAsync(client, "2026-06-15", "Food", "Essentials", -125m);

        var after = await client.GetFromJsonAsync<JsonElement>("/api/financial/dashboard");
        var balanceAfter = ObfuscationHelper.Deobfuscate(
            after.GetProperty("stats").GetProperty("totalBalance").GetString()!);

        Assert.Equal(balanceBefore - 125m, balanceAfter);
    }
}
