using System.Net;
using FinancialAppApi.Services.Documents;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class GcsDocumentVaultStoreTests
{
    [Fact]
    public async Task UploadAsync_SendsCorrectPostRequest()
    {
        var requests = new List<CapturedRequest>();
        var handler = new DelegateHandler(async request =>
        {
            requests.Add(await CapturedRequest.FromAsync(request));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        var store = NewStore(client, "test-bucket");
        var doc = new byte[] { 1, 2, 3 };

        await store.UploadAsync("user/2026/test.pdf", doc, "application/pdf");

        Assert.Collection(
            requests,
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/upload/storage/v1/b/test-bucket/o?uploadType=media&name=user%2F2026%2Ftest.pdf", request.Path);
                Assert.Equal("application/pdf", request.ContentType);
                Assert.Equal(doc, request.Bytes);
                Assert.Equal("Bearer test-access-token", request.Authorization);
            });
    }

    [Fact]
    public async Task DownloadAsync_SendsCorrectGetRequest_AndReturnsData()
    {
        var requests = new List<CapturedRequest>();
        var handler = new DelegateHandler(async request =>
        {
            requests.Add(await CapturedRequest.FromAsync(request));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });
        using var client = new HttpClient(handler);
        var store = NewStore(client, "test-bucket");

        var downloaded = await store.DownloadAsync("user/test doc.pdf");

        Assert.Equal(new byte[] { 1, 2, 3 }, downloaded);
        Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, requests[0].Method);
        // The object name is one encoded segment: the separator must be %2F, not a real slash.
        Assert.Equal("/storage/v1/b/test-bucket/o/user%2Ftest%20doc.pdf?alt=media", requests[0].Path);
        Assert.Equal("Bearer test-access-token", requests[0].Authorization);
    }

    [Fact]
    public async Task DownloadAsync_ReturnsNullOn404()
    {
        var handler = new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var client = new HttpClient(handler);
        var store = NewStore(client, "test-bucket");

        var downloaded = await store.DownloadAsync("missing.pdf");
        Assert.Null(downloaded);
    }

    [Fact]
    public async Task DeleteIfExistsAsync_SendsCorrectDeleteRequest()
    {
        var requests = new List<CapturedRequest>();
        var handler = new DelegateHandler(async request =>
        {
            requests.Add(await CapturedRequest.FromAsync(request));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var client = new HttpClient(handler);
        var store = NewStore(client, "test-bucket");

        await store.DeleteIfExistsAsync("user/old!doc.pdf");

        Assert.Single(requests);
        Assert.Equal(HttpMethod.Delete, requests[0].Method);
        Assert.Equal("/storage/v1/b/test-bucket/o/user%2Fold%21doc.pdf", requests[0].Path);
        Assert.Equal("Bearer test-access-token", requests[0].Authorization);
    }

    [Fact]
    public async Task DeleteIfExistsAsync_SucceedsOn404()
    {
        var handler = new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var client = new HttpClient(handler);
        var store = NewStore(client, "test-bucket");

        await store.DeleteIfExistsAsync("missing.pdf");
    }

    private static GcsDocumentVaultStore NewStore(HttpClient client, string bucket)
    {
        var options = new FixedOptionsMonitor<DocumentVaultOptions>(new DocumentVaultOptions
        {
            Enabled = true,
            Bucket = bucket
        });
        return new GcsDocumentVaultStore(
            new StubHttpClientFactory(client),
            options,
            NullLogger<GcsDocumentVaultStore>.Instance,
            _ => Task.FromResult("test-access-token"));
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Path,
        string? Authorization,
        string? ContentType,
        byte[] Bytes)
    {
        public static async Task<CapturedRequest> FromAsync(HttpRequestMessage request)
        {
            var bytes = request.Content == null
                ? []
                : await request.Content.ReadAsByteArrayAsync();
            return new CapturedRequest(
                request.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                bytes);
        }
    }
}
