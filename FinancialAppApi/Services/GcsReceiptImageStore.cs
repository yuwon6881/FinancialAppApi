using System.Net;
using System.Net.Http.Headers;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;

namespace FinancialAppApi.Services;

/// <summary>
/// Keeps transient receipt images in a private Google Cloud Storage bucket instead of consuming
/// the much smaller Postgres row budget. Authentication is Application Default Credentials, so on
/// Cloud Run the runtime service account is the only thing that can reach the bucket and there is
/// no storage secret to leak; callers reach receipt images only through the OCR API.
/// </summary>
public sealed class GcsReceiptImageStore : IReceiptImageStore
{
    public const string HttpClientName = "GcsReceiptImageStore";
    public const long MaxImageBytes = 10 * 1024 * 1024;

    private const string StorageScope = "https://www.googleapis.com/auth/devstorage.read_write";

    private static readonly string[] AllowedMimeTypes =
        ["image/jpeg", "image/png", "image/webp", "image/heic", "image/heif"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<ReceiptScanStorageOptions> _options;
    private readonly ILogger<GcsReceiptImageStore> _logger;
    private readonly Func<CancellationToken, Task<string>>? _accessTokenProvider;
    private GoogleCredential? _scopedCredential;

    public GcsReceiptImageStore(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<ReceiptScanStorageOptions> options,
        ILogger<GcsReceiptImageStore> logger)
        : this(httpClientFactory, options, logger, accessTokenProvider: null)
    {
    }

    internal GcsReceiptImageStore(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<ReceiptScanStorageOptions> options,
        ILogger<GcsReceiptImageStore> logger,
        Func<CancellationToken, Task<string>>? accessTokenProvider)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        _accessTokenProvider = accessTokenProvider;
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

        var bucket = RequireBucket();
        // ifGenerationMatch=0 fails the request when the object already exists, keeping the
        // no-overwrite guarantee a scan job depends on: one job id owns one object path.
        var uri = $"https://storage.googleapis.com/upload/storage/v1/b/{EncodeSegment(bucket)}/o" +
                  $"?uploadType=media&ifGenerationMatch=0&name={EncodeObjectPath(objectPath)}";

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Content = new ByteArrayContent(imageData);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);

        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "upload the receipt image", cancellationToken);
    }

    public async Task<byte[]?> DownloadAsync(
        string objectPath,
        CancellationToken cancellationToken = default)
    {
        var bucket = RequireBucket();
        var uri = $"https://storage.googleapis.com/storage/v1/b/{EncodeSegment(bucket)}/o/" +
                  $"{EncodeObjectPath(objectPath)}?alt=media";

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
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
        var bucket = RequireBucket();
        var uri = $"https://storage.googleapis.com/storage/v1/b/{EncodeSegment(bucket)}/o/" +
                  $"{EncodeObjectPath(objectPath)}";

        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        using var response = await SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }
        await EnsureSuccessAsync(response, "delete the receipt image", cancellationToken);
    }

    private string RequireBucket()
    {
        var bucket = _options.CurrentValue.Bucket?.Trim();
        if (string.IsNullOrWhiteSpace(bucket))
        {
            throw new ReceiptImageStoreException(
                "Receipt image storage is not configured. Set ReceiptScanStorage:Bucket.");
        }
        return bucket;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string accessToken;
        try
        {
            accessToken = await GetAccessTokenAsync(ct);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not obtain Application Default Credentials for GCS.");
            throw new ReceiptImageStoreException("Authentication token acquisition failed.", exception);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            return await _httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception exception) when (
            exception is HttpRequestException ||
            (exception is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new ReceiptImageStoreException("Cloud Storage could not be reached.", exception);
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_accessTokenProvider is not null)
        {
            return await _accessTokenProvider(ct);
        }

        _scopedCredential ??= await BuildScopedCredentialAsync(ct);
        return await _scopedCredential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: ct);
    }

    private static async Task<GoogleCredential> BuildScopedCredentialAsync(CancellationToken ct)
    {
        var credential = await GoogleCredential.GetApplicationDefaultAsync(ct);
        return credential.IsCreateScopedRequired ? credential.CreateScoped(StorageScope) : credential;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string action,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        _ = await response.Content.ReadAsStringAsync(ct);
        throw new ReceiptImageStoreException(
            $"Cloud Storage could not {action} (HTTP {(int)response.StatusCode}).");
    }

    /// <summary>
    /// Percent-encodes an object name for the GCS JSON API. The separators are encoded too
    /// (<c>a/b.jpg</c> becomes <c>a%2Fb.jpg</c>): in <c>/b/{bucket}/o/{object}</c> the name is a
    /// SINGLE path segment, so leaving real slashes in addresses a different resource and every
    /// read and delete comes back 404. Uploads would still appear to work, because there the name
    /// travels as a query parameter — which is exactly how this hides until data is unreachable.
    /// </summary>
    private static string EncodeObjectPath(string objectPath)
    {
        var segments = objectPath.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new ArgumentException("Storage object path is invalid.", nameof(objectPath));
        }
        return EncodeSegment(objectPath);
    }

    private static string EncodeSegment(string segment) => Uri.EscapeDataString(segment);
}
