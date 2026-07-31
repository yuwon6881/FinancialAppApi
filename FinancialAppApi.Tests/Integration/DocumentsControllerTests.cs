using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FinancialAppApi.Tests.Integration;

public sealed class DocumentsControllerTests : IntegrationTestBase
{
    [Fact]
    public async Task DocumentWorkflow_UploadsListsDownloadsUpdatesUsageAndDeletes()
    {
        var client = await CreateSignedInClientAsync();
        var originalBytes = Encoding.UTF8.GetBytes("%PDF-1.7 vault test");
        var categoryId = await AddCategoryAsync(client, 2026);

        using var uploadForm = CreateUploadForm(originalBytes, "tax evidence.pdf", categoryId);
        var uploadResponse = await client.PostAsync("/api/documents", uploadForm);

        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);
        var upload = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var documentId = upload.GetProperty("id").GetInt32();

        var listResponse = await client.GetAsync("/api/documents?taxYear=2026&search=evidence");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var listed = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(documentId, listed.GetProperty("id").GetInt32());
        Assert.Equal("application/pdf", listed.GetProperty("contentType").GetString());
        Assert.Equal("2033-12-31", listed.GetProperty("retentionUntil").GetString());

        var years = await client.GetFromJsonAsync<int[]>("/api/documents/years");
        Assert.NotNull(years);
        Assert.Equal(2026, years[0]);
        Assert.Contains(2000, years);

        var usageResponse = await client.GetAsync("/api/documents/usage");
        Assert.Equal(HttpStatusCode.OK, usageResponse.StatusCode);
        var usage = await usageResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(originalBytes.Length, usage.GetProperty("totalBytes").GetInt64());
        Assert.Equal(1, usage.GetProperty("documentCount").GetInt32());

        var downloadResponse = await client.GetAsync($"/api/documents/{documentId}/content");
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.Equal(originalBytes, await downloadResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", downloadResponse.Content.Headers.ContentType?.MediaType);

        var missingCategoryUpdate = await client.PatchAsJsonAsync($"/api/documents/{documentId}", new
        {
            reliefCategory = "",
            reliefCategorySpecified = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingCategoryUpdate.StatusCode);

        var updateResponse = await client.PatchAsJsonAsync($"/api/documents/{documentId}", new
        {
            taxYear = 2025,
            notes = "Filed copy",
            transactionId = "transaction-1",
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2025, updated.GetProperty("taxYear").GetInt32());
        Assert.Equal("Filed copy", updated.GetProperty("notes").GetString());
        Assert.Equal("transaction-1", updated.GetProperty("transactionId").GetString());
        Assert.Equal("2032-12-31", updated.GetProperty("retentionUntil").GetString());

        var unlinkResponse = await client.PatchAsJsonAsync($"/api/documents/{documentId}", new
        {
            notes = (string?)null,
            transactionId = (string?)null,
        });
        Assert.Equal(HttpStatusCode.OK, unlinkResponse.StatusCode);
        var unlinked = await unlinkResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(unlinked.TryGetProperty("notes", out _));
        Assert.False(unlinked.TryGetProperty("transactionId", out _));

        var deleteResponse = await client.DeleteAsync($"/api/documents/{documentId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/documents/{documentId}/content")).StatusCode);
    }

    [Fact]
    public async Task Upload_WithDisguisedExecutable_Returns400()
    {
        var client = await CreateSignedInClientAsync();
        var categoryId = await AddCategoryAsync(client, 2026);
        using var form = CreateUploadForm([0x4D, 0x5A, 0x90, 0x00], "receipt.jpg", categoryId);

        var response = await client.PostAsync("/api/documents", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BulkWorkflow_KeepsValidUploadReportsFailureSummarizesExportsAndDeletes()
    {
        var client = await CreateSignedInClientAsync();
        var categoryResponse = await client.PostAsJsonAsync("/api/documents/relief-categories/2025", new
        {
            name = "Lifestyle",
            limit = 2500m,
            detail = "User-maintained limit"
        });
        Assert.Equal(HttpStatusCode.OK, categoryResponse.StatusCode);
        var category = await categoryResponse.Content.ReadFromJsonAsync<JsonElement>();
        var categoryId = category.GetProperty("id").GetString();

        using var form = new MultipartFormDataContent();
        var valid = new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-1.7 valid"));
        valid.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(valid, "files", "valid.pdf");
        var invalid = new ByteArrayContent([0x4D, 0x5A, 0x00]);
        invalid.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(invalid, "files", "invalid.pdf");
        form.Add(new StringContent("2025"), "taxYear");
        form.Add(new StringContent(categoryId!), "reliefCategory");

        var response = await client.PostAsync("/api/documents/bulk", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var results = payload.GetProperty("results").EnumerateArray().ToList();
        Assert.True(results[0].GetProperty("uploaded").GetBoolean());
        Assert.False(results[1].GetProperty("uploaded").GetBoolean());
        var id = results[0].GetProperty("id").GetInt32();

        var confirm = await client.PatchAsJsonAsync($"/api/documents/{id}", new
        {
            amount = 2500m,
            amountSpecified = true,
            amountCurrency = "MYR",
            amountStatus = "Confirmed"
        });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var summary = await client.GetFromJsonAsync<JsonElement>("/api/documents/summary/2025");
        var lifestyle = Assert.Single(summary.GetProperty("categories").EnumerateArray());
        Assert.Equal(2500m, lifestyle.GetProperty("confirmedAmount").GetDecimal());

        var export = await client.GetAsync("/api/documents/export?taxYear=2025");
        Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await export.Content.ReadAsByteArrayAsync());

        var delete = await client.PostAsJsonAsync("/api/documents/bulk-delete", new { ids = new[] { id } });
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var deletePayload = await delete.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(deletePayload.GetProperty("results")[0].GetProperty("deleted").GetBoolean());
    }

    [Fact]
    public async Task Endpoints_RequireAuthentication()
    {
        var client = CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/documents/usage")).StatusCode);
    }

    private static async Task<string> AddCategoryAsync(HttpClient client, int taxYear)
    {
        var response = await client.PostAsJsonAsync($"/api/documents/relief-categories/{taxYear}", new
        {
            name = "Lifestyle",
            limit = 2500m,
            detail = "User-maintained limit"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var category = await response.Content.ReadFromJsonAsync<JsonElement>();
        return category.GetProperty("id").GetString()!;
    }

    private static MultipartFormDataContent CreateUploadForm(byte[] bytes, string fileName, string reliefCategory)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);
        content.Add(new StringContent("2026"), "taxYear");
        content.Add(new StringContent("Annual filing"), "notes");
        content.Add(new StringContent(reliefCategory), "reliefCategory");
        return content;
    }
}
