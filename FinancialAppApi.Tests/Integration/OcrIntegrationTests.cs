using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Tests.Integration;

/// <summary>
/// Receipt-scan (OCR) job workflow through the full HTTP stack. The background worker that would
/// call Gemini is removed by the test factory, so these tests drive validation, the queued-job
/// contract, idempotent terminal-state reads, explicit deletion, and worker-key gating without any AI dependency.
/// </summary>
public class OcrIntegrationTests : IntegrationTestBase
{
    private static MultipartFormDataContent ImageForm(byte[] bytes, string field = "image")
    {
        var content = new MultipartFormDataContent();
        var filePart = new ByteArrayContent(bytes);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(filePart, field, "receipt.jpg");
        return content;
    }

    [Fact]
    public async Task CreateScanJob_WithoutImage_Returns400()
    {
        var client = await CreateSignedInClientAsync();

        var response = await client.PostAsync("/api/ocr/scan-receipt/jobs", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateScanJob_WithImage_QueuesJob()
    {
        var client = await CreateSignedInClientAsync();

        var response = await client.PostAsync("/api/ocr/scan-receipt/jobs",
            ImageForm(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("ocr-", body.GetProperty("scanId").GetString());
        Assert.Equal("queued", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetScanJob_ForCompletedJob_IsIdempotentUntilExplicitlyDeleted()
    {
        var token = await SeedUserAndSessionAsync();
        var client = CreateAuthenticatedClient(token);

        const string jobId = "ocr-completed-1";
        await Factory.WithDbContextAsync(async db =>
        {
            db.ReceiptScanJobs.Add(new ReceiptScanJob
            {
                Id = jobId,
                Username = "alice",
                Status = "completed",
                ResultJson = """
                {"description":"Lunch","amount":23.45,"date":"2026-06-15","category":"Food","ledgerCategory":"Essentials","txType":"outflow"}
                """,
                CompletedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        });

        var first = await client.GetAsync($"/api/ocr/scan-receipt/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", body.GetProperty("status").GetString());
        var result = body.GetProperty("result");
        Assert.Equal("Lunch", result.GetProperty("description").GetString());
        // Amount is obfuscated on the wire even in the OCR result.
        Assert.Equal(23.45m, ObfuscationHelper.Deobfuscate(result.GetProperty("amount").GetString()!));

        // Polling is idempotent so a lost client response cannot consume the OCR result.
        var second = await client.GetAsync($"/api/ocr/scan-receipt/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var deleted = await client.DeleteAsync($"/api/ocr/scan-receipt/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var afterDelete = await client.GetAsync($"/api/ocr/scan-receipt/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
    }

    [Fact]
    public async Task GetScanJob_UnknownId_Returns404()
    {
        var client = await CreateSignedInClientAsync();

        var response = await client.GetAsync("/api/ocr/scan-receipt/jobs/ocr-nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetScanJob_BelongingToAnotherUser_Returns404()
    {
        var token = await SeedUserAndSessionAsync();
        var client = CreateAuthenticatedClient(token);
        await Factory.WithDbContextAsync(async db =>
        {
            db.ReceiptScanJobs.Add(new ReceiptScanJob
            {
                Id = "ocr-other-user",
                Username = "someone-else",
                Status = "queued",
            });
            await db.SaveChangesAsync();
        });

        var response = await client.GetAsync("/api/ocr/scan-receipt/jobs/ocr-other-user");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ProcessEndpoint_WithoutWorkerKeyConfigured_Returns503()
    {
        // No auth needed for this worker webhook; it is gated by the X-Ocr-Worker-Key header
        // against config. The test host does not configure OcrWorkerKey, so it is unavailable.
        var client = CreateClient();

        var response = await client.PostAsync("/api/ocr/scan-receipt/jobs/ocr-any/process", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
