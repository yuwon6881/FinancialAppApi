using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FinancialAppApi.Services;

public interface IReceiptImageStore
{
    Task UploadAsync(
        string objectPath,
        byte[] imageData,
        string mimeType,
        CancellationToken cancellationToken = default);

    Task<byte[]?> DownloadAsync(
        string objectPath,
        CancellationToken cancellationToken = default);

    Task DeleteIfExistsAsync(
        string objectPath,
        CancellationToken cancellationToken = default);
}

public sealed class ReceiptImageStoreException : Exception
{
    public ReceiptImageStoreException(string message) : base(message)
    {
    }

    public ReceiptImageStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Keeps transient receipt images in a private Supabase Storage bucket instead of
/// consuming the much smaller Postgres database quota. Only the backend receives
/// the secret API key; callers interact with receipt images through the OCR API.
/// </summary>
public sealed class SupabaseReceiptImageStore : IReceiptImageStore
{
    public const string HttpClientName = "SupabaseReceiptStorage";
    public const long MaxImageBytes = 10 * 1024 * 1024;

    private static readonly string[] AllowedMimeTypes =
        ["image/jpeg", "image/png", "image/webp", "image/heic", "image/heif"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SupabaseReceiptImageStore> _logger;
    private readonly SemaphoreSlim _bucketLock = new(1, 1);
    private bool _bucketReady;

    public SupabaseReceiptImageStore(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<SupabaseReceiptImageStore> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task UploadAsync(
        string objectPath,
        byte[] imageData,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        if (imageData.Length == 0 || imageData.LongLength > MaxImageBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(imageData),
                $"Receipt images must be between 1 byte and {MaxImageBytes} bytes.");
        }
        if (!AllowedMimeTypes.Contains(mimeType, StringComparer.Ordinal))
        {
            throw new ArgumentException("Unsupported receipt image MIME type.", nameof(mimeType));
        }

        var settings = GetSettings();
        await EnsurePrivateBucketAsync(settings, cancellationToken);

        using var request = CreateRequest(
            HttpMethod.Post,
            BuildStorageUri(settings, $"object/{EncodeSegment(settings.Bucket)}/{EncodeObjectPath(objectPath)}"),
            settings.ApiKey);
        request.Headers.TryAddWithoutValidation("x-upsert", "false");
        request.Content = new ByteArrayContent(imageData);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);

        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "upload the receipt image", cancellationToken);
    }

    public async Task<byte[]?> DownloadAsync(
        string objectPath,
        CancellationToken cancellationToken = default)
    {
        var settings = GetSettings();
        using var request = CreateRequest(
            HttpMethod.Get,
            BuildStorageUri(settings, $"object/authenticated/{EncodeSegment(settings.Bucket)}/{EncodeObjectPath(objectPath)}"),
            settings.ApiKey);
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureSuccessAsync(response, "download the receipt image", cancellationToken);

        if (response.Content.Headers.ContentLength is > MaxImageBytes)
        {
            throw new ReceiptImageStoreException("The stored receipt image exceeds the configured size limit.");
        }

        var imageData = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (imageData.LongLength > MaxImageBytes)
        {
            throw new ReceiptImageStoreException("The stored receipt image exceeds the configured size limit.");
        }
        return imageData;
    }

    public async Task DeleteIfExistsAsync(
        string objectPath,
        CancellationToken cancellationToken = default)
    {
        var settings = GetSettings();
        using var request = CreateRequest(
            HttpMethod.Delete,
            BuildStorageUri(settings, $"object/{EncodeSegment(settings.Bucket)}/{EncodeObjectPath(objectPath)}"),
            settings.ApiKey);
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }
        await EnsureSuccessAsync(response, "delete the receipt image", cancellationToken);
    }

    private async Task EnsurePrivateBucketAsync(
        StorageSettings settings,
        CancellationToken cancellationToken)
    {
        if (_bucketReady) return;

        await _bucketLock.WaitAsync(cancellationToken);
        try
        {
            if (_bucketReady) return;

            using var getRequest = CreateRequest(
                HttpMethod.Get,
                BuildStorageUri(settings, $"bucket/{EncodeSegment(settings.Bucket)}"),
                settings.ApiKey);
            using var getResponse = await SendAsync(getRequest, cancellationToken);

            if (getResponse.StatusCode == HttpStatusCode.NotFound)
            {
                using var createRequest = CreateRequest(
                    HttpMethod.Post,
                    BuildStorageUri(settings, "bucket"),
                    settings.ApiKey);
                createRequest.Content = JsonContent.Create(new
                {
                    id = settings.Bucket,
                    name = settings.Bucket,
                    @public = false,
                    file_size_limit = MaxImageBytes,
                    allowed_mime_types = AllowedMimeTypes
                });
                using var createResponse = await SendAsync(createRequest, cancellationToken);
                if (createResponse.StatusCode == HttpStatusCode.Conflict)
                {
                    // Another instance may have created the bucket concurrently.
                    // Re-read it and still enforce the private-bucket requirement.
                    using var verifyRequest = CreateRequest(
                        HttpMethod.Get,
                        BuildStorageUri(settings, $"bucket/{EncodeSegment(settings.Bucket)}"),
                        settings.ApiKey);
                    using var verifyResponse = await SendAsync(verifyRequest, cancellationToken);
                    await EnsureSuccessAsync(verifyResponse, "inspect the receipt bucket", cancellationToken);
                    await EnsureBucketIsPrivateAsync(verifyResponse, settings.Bucket, cancellationToken);
                }
                else
                {
                    await EnsureSuccessAsync(createResponse, "create the private receipt bucket", cancellationToken);
                }
            }
            else
            {
                await EnsureSuccessAsync(getResponse, "inspect the receipt bucket", cancellationToken);
                await EnsureBucketIsPrivateAsync(getResponse, settings.Bucket, cancellationToken);
            }

            _bucketReady = true;
            _logger.LogInformation(
                "Supabase Storage receipt bucket {Bucket} is ready for private OCR uploads.",
                settings.Bucket);
        }
        finally
        {
            _bucketLock.Release();
        }
    }

    private StorageSettings GetSettings()
    {
        var projectUrl = _configuration["SupabaseStorage:ProjectUrl"]?.Trim().TrimEnd('/');
        var apiKey = _configuration["SupabaseStorage:ApiKey"]?.Trim();
        var bucket = _configuration["SupabaseStorage:ReceiptBucket"]?.Trim();
        if (string.IsNullOrWhiteSpace(bucket)) bucket = "receipt-scans";

        if (string.IsNullOrWhiteSpace(projectUrl) ||
            !Uri.TryCreate(projectUrl, UriKind.Absolute, out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttps && parsedUrl.Scheme != Uri.UriSchemeHttp))
        {
            throw new ReceiptImageStoreException(
                "Supabase receipt storage is not configured. Set SupabaseStorage:ProjectUrl.");
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ReceiptImageStoreException(
                "Supabase receipt storage is not configured. Set SupabaseStorage:ApiKey to a backend secret key.");
        }
        if (bucket.Length > 100 || bucket.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ReceiptImageStoreException("SupabaseStorage:ReceiptBucket contains invalid characters.");
        }

        return new StorageSettings(projectUrl, apiKey, bucket);
    }

    private static async Task EnsureBucketIsPrivateAsync(
        HttpResponseMessage response,
        string bucket,
        CancellationToken cancellationToken)
    {
        try
        {
            var bucketJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(bucketJson);
            if (document.RootElement.TryGetProperty("public", out var publicProperty) &&
                publicProperty.ValueKind == JsonValueKind.True)
            {
                throw new ReceiptImageStoreException(
                    $"Supabase Storage bucket '{bucket}' must be private before receipt images can be uploaded.");
            }
        }
        catch (JsonException exception)
        {
            throw new ReceiptImageStoreException("Supabase Storage returned invalid bucket metadata.", exception);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string apiKey)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("apikey", apiKey);

        // New sb_secret_* keys are opaque and must be sent only as apikey. Legacy
        // service_role JWTs still require the Authorization header for RLS bypass.
        if (!apiKey.StartsWith("sb_secret_", StringComparison.Ordinal))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException ||
            (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ReceiptImageStoreException("Supabase receipt storage could not be reached.", exception);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string action,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        // Consume the small Storage API error response so the connection can be reused,
        // but do not include it in exceptions because it may contain internal details.
        _ = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new ReceiptImageStoreException(
            $"Supabase Storage could not {action} (HTTP {(int)response.StatusCode}).");
    }

    private static Uri BuildStorageUri(StorageSettings settings, string path) =>
        new($"{settings.ProjectUrl}/storage/v1/{path}", UriKind.Absolute);

    private static string EncodeObjectPath(string objectPath)
    {
        var segments = objectPath.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new ArgumentException("Storage object path is invalid.", nameof(objectPath));
        }
        return string.Join('/', segments.Select(EncodeSegment));
    }

    private static string EncodeSegment(string segment) => Uri.EscapeDataString(segment);

    private sealed record StorageSettings(string ProjectUrl, string ApiKey, string Bucket);
}
