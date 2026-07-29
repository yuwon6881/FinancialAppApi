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

        using var uploadForm = CreateUploadForm(originalBytes, "tax evidence.pdf");
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
        Assert.Equal([2026], years);

        var usageResponse = await client.GetAsync("/api/documents/usage");
        Assert.Equal(HttpStatusCode.OK, usageResponse.StatusCode);
        var usage = await usageResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(originalBytes.Length, usage.GetProperty("totalBytes").GetInt64());
        Assert.Equal(1, usage.GetProperty("documentCount").GetInt32());

        var downloadResponse = await client.GetAsync($"/api/documents/{documentId}/content");
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.Equal(originalBytes, await downloadResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", downloadResponse.Content.Headers.ContentType?.MediaType);

        var updateResponse = await client.PatchAsJsonAsync($"/api/documents/{documentId}", new
        {
            taxYear = 2025,
            documentType = "Tax Return",
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
    public async Task DocumentTypes_CanBeAddedSearchedClientSideAndDeletedWhenUnused()
    {
        var client = await CreateSignedInClientAsync();

        var defaults = await client.GetFromJsonAsync<JsonElement>("/api/document-types");
        Assert.Contains(defaults.EnumerateArray(), item => item.GetProperty("name").GetString() == "Receipt");

        var create = await client.PostAsJsonAsync("/api/document-types", new { name = "Education Receipt" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        var delete = await client.DeleteAsync($"/api/document-types/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Upload_WithDisguisedExecutable_Returns400()
    {
        var client = await CreateSignedInClientAsync();
        using var form = CreateUploadForm([0x4D, 0x5A, 0x90, 0x00], "receipt.jpg");

        var response = await client.PostAsync("/api/documents", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Endpoints_RequireAuthentication()
    {
        var client = CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/documents/usage")).StatusCode);
    }

    private static MultipartFormDataContent CreateUploadForm(byte[] bytes, string fileName)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);
        content.Add(new StringContent("2026"), "taxYear");
        content.Add(new StringContent("Receipt"), "documentType");
        content.Add(new StringContent("Annual filing"), "notes");
        return content;
    }
}
