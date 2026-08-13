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

        var listResponse = await client.GetAsync("/api/documents?taxYear=2026");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var listed = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(documentId, listed.GetProperty("id").GetInt32());
        Assert.Equal("application/pdf", listed.GetProperty("contentType").GetString());
        Assert.Equal("2033-12-31", listed.GetProperty("retentionUntil").GetString());
        Assert.False(listed.TryGetProperty("notes", out _));

        var filteredResponse = await client.GetAsync($"/api/documents?taxYear=2026&reliefCategory={categoryId}&sort=name-asc");
        Assert.Equal(HttpStatusCode.OK, filteredResponse.StatusCode);
        var filtered = await filteredResponse.Content.ReadFromJsonAsync<JsonElement>();
        var filteredItems = filtered.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(filteredItems);
        Assert.Equal(documentId, filteredItems[0].GetProperty("id").GetInt32());

        var years = await client.GetFromJsonAsync<int[]>("/api/documents/years");
        Assert.NotNull(years);
        Assert.Equal([2026], years);
        Assert.DoesNotContain(2000, years);

        var usageResponse = await client.GetAsync("/api/documents/usage");
        Assert.Equal(HttpStatusCode.OK, usageResponse.StatusCode);
        var usage = await usageResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(originalBytes.Length, usage.GetProperty("totalBytes").GetInt64());
        Assert.Equal(1, usage.GetProperty("documentCount").GetInt32());

        var overview = await client.GetFromJsonAsync<JsonElement>("/api/documents/overview");
        Assert.Equal(2026, overview.GetProperty("selectedTaxYear").GetInt32());
        Assert.Equal(1, overview.GetProperty("usage").GetProperty("documentCount").GetInt32());
        Assert.Equal([2026], overview.GetProperty("availableYears").EnumerateArray().Select(year => year.GetInt32()));
        Assert.Single(overview.GetProperty("reliefCategories").EnumerateArray());

        var downloadResponse = await client.GetAsync($"/api/documents/{documentId}/content");
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.Equal(originalBytes, await downloadResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", downloadResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", downloadResponse.Content.Headers.ContentDisposition?.DispositionType);

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/documents/{documentId}/content");
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 9);
        using var rangeResponse = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(0, 9, originalBytes.Length), rangeResponse.Content.Headers.ContentRange);
        Assert.Equal(originalBytes[..10], await rangeResponse.Content.ReadAsByteArrayAsync());

        var missingCategoryUpdate = await client.PatchAsJsonAsync($"/api/documents/{documentId}", new
        {
            reliefCategory = "",
            reliefCategorySpecified = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingCategoryUpdate.StatusCode);
        await AssertCarriesAReason(missingCategoryUpdate);

        // A change the server will not make must say so as a 400 with a reason. This used to be a
        // bare 404, i.e. "that document does not exist", for a document sitting on screen.
        var badYearUpdate = await client.PatchAsJsonAsync($"/api/documents/{documentId}", new { taxYear = 1990 });
        Assert.Equal(HttpStatusCode.BadRequest, badYearUpdate.StatusCode);
        await AssertCarriesAReason(badYearUpdate);

        var missingDocumentUpdate = await client.PatchAsJsonAsync($"/api/documents/{documentId + 100000}", new { taxYear = 2025 });
        Assert.Equal(HttpStatusCode.NotFound, missingDocumentUpdate.StatusCode);
        await AssertCarriesAReason(missingDocumentUpdate);

        var updateResponse = await client.PatchAsJsonAsync($"/api/documents/{documentId}", new
        {
            taxYear = 2025,
            transactionId = "transaction-1",
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2025, updated.GetProperty("taxYear").GetInt32());
        Assert.False(updated.TryGetProperty("notes", out _));
        Assert.Equal("transaction-1", updated.GetProperty("transactionId").GetString());
        Assert.Equal("2032-12-31", updated.GetProperty("retentionUntil").GetString());

        var unlinkResponse = await client.PatchAsJsonAsync($"/api/documents/{documentId}", new
        {
            transactionId = (string?)null,
        });
        Assert.Equal(HttpStatusCode.OK, unlinkResponse.StatusCode);
        var unlinked = await unlinkResponse.Content.ReadFromJsonAsync<JsonElement>();
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

        var selectedExport = await client.PostAsJsonAsync("/api/documents/export-selected", new { ids = new[] { id } });
        Assert.Equal(HttpStatusCode.OK, selectedExport.StatusCode);
        Assert.NotEmpty(await selectedExport.Content.ReadAsByteArrayAsync());

        var staleSelectedExport = await client.PostAsJsonAsync("/api/documents/export-selected", new { ids = new[] { id, id + 100000 } });
        Assert.Equal(HttpStatusCode.NotFound, staleSelectedExport.StatusCode);

        var delete = await client.PostAsJsonAsync("/api/documents/bulk-delete", new { ids = new[] { id } });
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var deletePayload = await delete.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(deletePayload.GetProperty("results")[0].GetProperty("deleted").GetBoolean());
    }

    [Fact]
    public async Task BulkCategoryUpdate_UpdatesMultipleDocumentsAndReportsInvalidRows()
    {
        var client = await CreateSignedInClientAsync();
        var lifestyleId = await AddCategoryAsync(client, 2026);
        var educationResponse = await client.PostAsJsonAsync("/api/documents/relief-categories/2026", new
        {
            name = "Education",
            limit = 7000m,
            detail = "Education expenses"
        });
        Assert.Equal(HttpStatusCode.OK, educationResponse.StatusCode);
        var education = await educationResponse.Content.ReadFromJsonAsync<JsonElement>();
        var educationId = education.GetProperty("id").GetString()!;

        using var firstForm = CreateUploadForm(Encoding.UTF8.GetBytes("%PDF-1.7 first"), "first.pdf", lifestyleId);
        using var secondForm = CreateUploadForm(Encoding.UTF8.GetBytes("%PDF-1.7 second"), "second.pdf", lifestyleId);
        var firstUpload = await client.PostAsync("/api/documents", firstForm);
        var secondUpload = await client.PostAsync("/api/documents", secondForm);
        Assert.Equal(HttpStatusCode.OK, firstUpload.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondUpload.StatusCode);
        var firstId = (await firstUpload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var secondId = (await secondUpload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var response = await client.PostAsJsonAsync("/api/documents/bulk-update-categories", new
        {
            updates = new[]
            {
                new { id = firstId, reliefCategory = educationId },
                new { id = secondId, reliefCategory = "not-configured" }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results").EnumerateArray().ToList();
        Assert.True(results[0].GetProperty("updated").GetBoolean());
        Assert.False(results[1].GetProperty("updated").GetBoolean());

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/documents?taxYear=2026&take=100");
        var firstListed = listed.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetInt32() == firstId);
        var secondListed = listed.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetInt32() == secondId);
        Assert.Equal(educationId, firstListed.GetProperty("reliefCategory").GetString());
        Assert.Equal(lifestyleId, secondListed.GetProperty("reliefCategory").GetString());
    }

    [Fact]
    public async Task Endpoints_RequireAuthentication()
    {
        var client = CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/documents/usage")).StatusCode);
    }

    [Fact]
    public async Task RetentionReview_ReportsTheKeepPeriodAndItsNoticeWindow()
    {
        var client = await CreateSignedInClientAsync();

        var response = await client.GetAsync("/api/documents/retention");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var review = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, review.GetProperty("taxYears").ValueKind);
        Assert.True(review.GetProperty("noticeWindowDays").GetInt32() > 0);
        Assert.Equal(7, review.GetProperty("keepYears").GetInt32());
    }

    /// <summary>
    /// A refusal with an empty body leaves the client nothing to show but its own generic fallback,
    /// which is the regression these assertions exist to catch.
    /// </summary>
    private static async Task AssertCarriesAReason(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("message", out var message));
        Assert.False(string.IsNullOrWhiteSpace(message.GetString()));
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
        content.Add(new StringContent(reliefCategory), "reliefCategory");
        return content;
    }
}
