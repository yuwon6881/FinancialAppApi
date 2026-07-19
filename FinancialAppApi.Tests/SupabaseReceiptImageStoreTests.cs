using System.Net;
using System.Text;
using System.Text.Json;
using FinancialAppApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class SupabaseReceiptImageStoreTests
{
    [Fact]
    public async Task UploadAsync_CreatesPrivateBucketAndUploadsWithBackendSecretKey()
    {
        var requests = new List<CapturedRequest>();
        var handler = new DelegateHandler(async request =>
        {
            requests.Add(await CapturedRequest.FromAsync(request));
            return requests.Count switch
            {
                // Supabase currently wraps the missing-bucket 404 in HTTP 400.
                1 => JsonResponse(HttpStatusCode.BadRequest,
                    """{"statusCode":"404","error":"Bucket not found","message":"Bucket not found"}"""),
                2 => JsonResponse(HttpStatusCode.OK, """{"name":"receipt-scans"}"""),
                3 => JsonResponse(HttpStatusCode.OK, """{"Key":"receipt-scans/user/scan.jpg"}"""),
                _ => throw new InvalidOperationException("Unexpected request.")
            };
        });
        using var client = new HttpClient(handler);
        var store = NewStore(client, "sb_secret_backend-test");
        var image = new byte[] { 0xFF, 0xD8, 0xFF, 1, 2, 3 };

        await store.UploadAsync("user/scan.jpg", image, "image/jpeg");

        Assert.Collection(
            requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/storage/v1/bucket/receipt-scans", request.Path);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/storage/v1/bucket", request.Path);
                using var json = JsonDocument.Parse(request.Body);
                Assert.False(json.RootElement.GetProperty("public").GetBoolean());
                Assert.Equal(SupabaseReceiptImageStore.MaxImageBytes,
                    json.RootElement.GetProperty("file_size_limit").GetInt64());
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/storage/v1/object/receipt-scans/user/scan.jpg", request.Path);
                Assert.Equal("image/jpeg", request.ContentType);
                Assert.Equal(image, request.Bytes);
            });
        Assert.All(requests, request =>
        {
            Assert.Equal("sb_secret_backend-test", request.ApiKey);
            Assert.Null(request.Authorization);
        });
    }

    [Fact]
    public async Task UploadAsync_WhenExistingBucketIsPublic_FailsClosed()
    {
        var handler = new DelegateHandler(_ => Task.FromResult(
            JsonResponse(HttpStatusCode.OK, """{"name":"receipt-scans","public":true}""")));
        using var client = new HttpClient(handler);
        var store = NewStore(client, "sb_secret_backend-test");

        var exception = await Assert.ThrowsAsync<ReceiptImageStoreException>(() =>
            store.UploadAsync("user/scan.jpg", [0xFF, 0xD8, 0xFF], "image/jpeg"));

        Assert.Contains("must be private", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadAndDelete_UseAuthenticatedObjectEndpointsAndLegacyBearerKey()
    {
        var requests = new List<CapturedRequest>();
        var handler = new DelegateHandler(async request =>
        {
            requests.Add(await CapturedRequest.FromAsync(request));
            return request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler);
        var store = NewStore(client, "legacy-service-role-jwt");

        var downloaded = await store.DownloadAsync("user/scan name.jpg");
        await store.DeleteIfExistsAsync("user/scan name.jpg");

        Assert.Equal(new byte[] { 1, 2, 3 }, downloaded);
        Assert.Equal("/storage/v1/object/authenticated/receipt-scans/user/scan%20name.jpg", requests[0].Path);
        Assert.Equal("/storage/v1/object/receipt-scans/user/scan%20name.jpg", requests[1].Path);
        Assert.All(requests, request =>
        {
            Assert.Equal("legacy-service-role-jwt", request.ApiKey);
            Assert.Equal("Bearer legacy-service-role-jwt", request.Authorization);
        });
    }

    private static SupabaseReceiptImageStore NewStore(HttpClient client, string apiKey)
    {
        var configuration = TestHelpers.NewConfiguration(
            ("SupabaseStorage:ProjectUrl", "https://example.supabase.co"),
            ("SupabaseStorage:ApiKey", apiKey),
            ("SupabaseStorage:ReceiptBucket", "receipt-scans"));
        return new SupabaseReceiptImageStore(
            new StubHttpClientFactory(client),
            configuration,
            NullLogger<SupabaseReceiptImageStore>.Instance);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

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
        public string Body => Encoding.UTF8.GetString(Bytes);

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
