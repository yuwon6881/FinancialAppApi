using System.Net;
using System.Net.Http.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace FinancialAppApi.Tests.Integration;

/// <summary>
/// ETag / If-None-Match behaviour for API GETs. The client re-reads these endpoints after every
/// sync, so an unchanged payload should cost a 304 rather than a full download and re-parse.
/// </summary>
public class ConditionalGetIntegrationTests : IntegrationTestBase
{
    private static async Task PostTransactionAsync(
        HttpClient client, string date, string category, string ledgerCategory, decimal amount)
    {
        var response = await client.PostAsJsonAsync("/api/transactions", new
        {
            date,
            description = $"seed-{date}-{amount}",
            category,
            ledgerCategory,
            amount = ObfuscationHelper.Obfuscate(amount),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ApiGet_ReturnsAWeakETagAndRevalidatingCacheControl()
    {
        var client = await CreateSignedInClientAsync();

        var response = await client.GetAsync("/api/financial/dashboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var etag = response.Headers.ETag;
        Assert.NotNull(etag);
        Assert.True(etag!.IsWeak);

        var cacheControl = response.Headers.CacheControl;
        Assert.NotNull(cacheControl);
        // private: never a shared cache, since every payload is one user's financial data.
        Assert.True(cacheControl!.Private);
        // no-cache (not no-store): the client keeps a copy but must revalidate, which is what
        // makes the 304 possible at all.
        Assert.True(cacheControl.NoCache);
        Assert.False(cacheControl.NoStore);
        Assert.False(cacheControl.Public);
    }

    [Fact]
    public async Task RepeatedGet_WithMatchingIfNoneMatch_Returns304WithNoBody()
    {
        var client = await CreateSignedInClientAsync();

        var first = await client.GetAsync("/api/financial/dashboard");
        var etag = first.Headers.ETag!.ToString();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/financial/dashboard");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ChangedData_ProducesANewETagAndAFullResponse()
    {
        var client = await CreateSignedInClientAsync();

        // `all=true` sidesteps cycle boundaries: whichever cycle a date falls in, a newly
        // created transaction is in this response. Scoping to a cycle instead makes the test
        // depend on how the cycle day maps a label to a date range, and a payload that
        // genuinely did not change *should* still revalidate.
        const string url = "/api/transactions?all=true";

        var first = await client.GetAsync(url);
        var firstETag = first.Headers.ETag!.ToString();
        var firstBody = await first.Content.ReadAsStringAsync();

        await PostTransactionAsync(client, "2026-07-11", "Food", "Essentials", -33.25m);

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("If-None-Match", firstETag);
        var second = await client.SendAsync(request);

        // The payload changed, so the stale tag must not be honoured.
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotEqual(firstETag, second.Headers.ETag!.ToString());
        var secondBody = await second.Content.ReadAsStringAsync();
        Assert.NotEqual(firstBody, secondBody);
        // PostTransactionAsync builds the description from the date and amount.
        Assert.Contains("seed-2026-07-11", secondBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleIfNoneMatch_IsIgnored()
    {
        var client = await CreateSignedInClientAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/financial/dashboard");
        request.Headers.TryAddWithoutValidation("If-None-Match", "W/\"deadbeefdeadbeefdeadbeefdeadbeef\"");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Bootstrap_AlsoRevalidates()
    {
        var client = await CreateSignedInClientAsync();

        var first = await client.GetAsync("/api/bootstrap");
        Assert.NotNull(first.Headers.ETag);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/bootstrap");
        request.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag!.ToString());
        var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task DocumentContent_UsesStoredHashAndSkipsStorageForA304()
    {
        var client = await CreateSignedInClientAsync();
        var documentId = 0;
        await Factory.WithDbContextAsync(async db =>
        {
            var document = new VaultDocument
            {
                OriginalFileName = "receipt.txt",
                ContentType = "text/plain",
                SizeBytes = 7,
                TaxYear = 2026,
                ReliefCategory = "other",
                AmountCurrency = "MYR",
                AmountStatus = "Confirmed",
                StorageObjectPath = "vault/receipt.txt",
                Sha256 = "stored-sha256",
                UploadedAt = DateTime.UtcNow,
                RetentionUntil = new DateOnly(2033, 12, 31)
            };
            db.VaultDocuments.Add(document);
            await db.SaveChangesAsync();
            documentId = document.Id;
        });
        var store = (FakeDocumentVaultStore)Factory.Services.GetRequiredService<IDocumentVaultStore>();
        store.Objects["vault/receipt.txt"] = "content"u8.ToArray();

        var first = await client.GetAsync($"/api/documents/{documentId}/content");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("\"stored-sha256\"", first.Headers.ETag?.ToString());
        Assert.Equal(1, store.DownloadCalls);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/documents/{documentId}/content");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"stored-sha256\"");
        var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(1, store.DownloadCalls);
    }

    [Fact]
    public async Task LoginResponse_IsNeverGivenAnETag()
    {
        // Auth responses carry Set-Cookie. A 304 would drop the cookie, so they must never be
        // made revalidatable — and they are POSTs, which this middleware ignores outright.
        var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "nobody",
            password = "wrong-password",
        });

        Assert.Null(response.Headers.ETag);
    }

    [Fact]
    public async Task MutatingRequests_AreNotGivenETags()
    {
        var client = await CreateSignedInClientAsync();

        var response = await client.PostAsJsonAsync("/api/transactions", new
        {
            date = "2026-07-12",
            description = "no-etag-on-post",
            category = "Food",
            ledgerCategory = "Essentials",
            amount = ObfuscationHelper.Obfuscate(-5m),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(response.Headers.ETag);
    }
}
