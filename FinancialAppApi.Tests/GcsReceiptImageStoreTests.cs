using System.Net;
using FinancialAppApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class GcsReceiptImageStoreTests
{
    [Fact]
    public async Task UploadAsync_PostsWithAdcBearerTokenAndRefusesToOverwrite()
    {
        var requests = new List<CapturedRequest>();
        var handler = new DelegateHandler(async request =>
        {
            requests.Add(await CapturedRequest.FromAsync(request));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        var store = NewStore(client, "test-receipts");
        var image = new byte[] { 0xFF, 0xD8, 0xFF, 1, 2, 3 };

        await store.UploadAsync("user/scan.jpg", image, "image/jpeg");

        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        // ifGenerationMatch=0 is what keeps one job id bound to one object: a second upload to the
        // same path must fail rather than silently replace the first image.
        Assert.Equal(
            "/upload/storage/v1/b/test-receipts/o?uploadType=media&ifGenerationMatch=0&name=user%2Fscan.jpg",
            request.Path);
        Assert.Equal("image/jpeg", request.ContentType);
        Assert.Equal(image, request.Bytes);
        Assert.Equal("Bearer test-access-token", request.Authorization);
        // No storage secret exists any more: the runtime service account is the only credential.
        Assert.Null(request.ApiKey);
    }

    [Fact]
    public async Task DownloadAndDelete_AddressTheObjectAsOneEncodedSegment()
    {
        var requests = new List<CapturedRequest>();
        var handler = new DelegateHandler(async request =>
        {
            requests.Add(await CapturedRequest.FromAsync(request));
            return request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var client = new HttpClient(handler);
        var store = NewStore(client, "test-receipts");

        var downloaded = await store.DownloadAsync("user/scan name.jpg");
        await store.DeleteIfExistsAsync("user/scan name.jpg");

        Assert.Equal(new byte[] { 1, 2, 3 }, downloaded);
        // The separator must be %2F, not a real slash: in /b/{bucket}/o/{object} the name is a
        // single path segment, so a literal slash addresses a different resource and 404s.
        Assert.Equal("/storage/v1/b/test-receipts/o/user%2Fscan%20name.jpg?alt=media", requests[0].Path);
        Assert.Equal("/storage/v1/b/test-receipts/o/user%2Fscan%20name.jpg", requests[1].Path);
        Assert.All(requests, request => Assert.Equal("Bearer test-access-token", request.Authorization));
    }

    [Fact]
    public async Task DownloadAsync_ReturnsNullWhenTheObjectIsGone()
    {
        var handler = new DelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var client = new HttpClient(handler);
        var store = NewStore(client, "test-receipts");

        Assert.Null(await store.DownloadAsync("user/scan.jpg"));
    }

    [Fact]
    public async Task DeleteIfExistsAsync_TreatsAMissingObjectAsSuccess()
    {
        var handler = new DelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var client = new HttpClient(handler);
        var store = NewStore(client, "test-receipts");

        // Cleanup runs on every terminal OCR state and may run twice for the same job.
        await store.DeleteIfExistsAsync("user/scan.jpg");
    }

    [Fact]
    public async Task UploadAsync_RejectsAnUnsupportedMimeTypeBeforeAnyRequest()
    {
        var handler = new DelegateHandler(_ =>
            throw new InvalidOperationException("The store must not reach the network."));
        using var client = new HttpClient(handler);
        var store = NewStore(client, "test-receipts");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.UploadAsync("user/scan.svg", [1, 2, 3], "image/svg+xml"));
    }

    [Fact]
    public async Task UploadAsync_RejectsAnEmptyImageBeforeAnyRequest()
    {
        var handler = new DelegateHandler(_ =>
            throw new InvalidOperationException("The store must not reach the network."));
        using var client = new HttpClient(handler);
        var store = NewStore(client, "test-receipts");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.UploadAsync("user/scan.jpg", [], "image/jpeg"));
    }

    [Fact]
    public async Task AnUnconfiguredBucketFailsClosed()
    {
        var handler = new DelegateHandler(_ =>
            throw new InvalidOperationException("The store must not reach the network."));
        using var client = new HttpClient(handler);
        var store = NewStore(client, bucket: "");

        var exception = await Assert.ThrowsAsync<ReceiptImageStoreException>(() =>
            store.UploadAsync("user/scan.jpg", [0xFF, 0xD8, 0xFF], "image/jpeg"));

        Assert.Contains("not configured", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnObjectPathWithATraversalSegmentIsRejected()
    {
        var handler = new DelegateHandler(_ =>
            throw new InvalidOperationException("The store must not reach the network."));
        using var client = new HttpClient(handler);
        var store = NewStore(client, "test-receipts");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.DownloadAsync("user/../other/scan.jpg"));
    }

    private static GcsReceiptImageStore NewStore(HttpClient client, string bucket)
    {
        var options = new FixedOptionsMonitor<ReceiptScanStorageOptions>(
            new ReceiptScanStorageOptions { Bucket = bucket });
        return new GcsReceiptImageStore(
            new StubHttpClientFactory(client),
            options,
            NullLogger<GcsReceiptImageStore>.Instance,
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
        string? ApiKey,
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
                request.Headers.TryGetValues("apikey", out var values) ? values.Single() : null,
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                bytes);
        }
    }
}
