using System.Net;
using System.Net.Http.Headers;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;

namespace FinancialAppApi.Services.Documents;

/// <summary>
/// Stores vault documents in a private Google Cloud Storage bucket, authenticating via Application Default Credentials.
/// </summary>
public sealed class GcsDocumentVaultStore : IDocumentVaultStore
{
    public const string HttpClientName = "GcsDocumentVaultStore";
    private const string StorageScope = "https://www.googleapis.com/auth/devstorage.read_write";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<DocumentVaultOptions> _options;
    private readonly ILogger<GcsDocumentVaultStore> _logger;
    private readonly Func<CancellationToken, Task<string>>? _accessTokenProvider;
    private GoogleCredential? _scopedCredential;

    public GcsDocumentVaultStore(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<DocumentVaultOptions> options,
        ILogger<GcsDocumentVaultStore> logger)
        : this(httpClientFactory, options, logger, accessTokenProvider: null)
    {
    }

    internal GcsDocumentVaultStore(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<DocumentVaultOptions> options,
        ILogger<GcsDocumentVaultStore> logger,
        Func<CancellationToken, Task<string>>? accessTokenProvider)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        _accessTokenProvider = accessTokenProvider;
    }

    public bool IsConfigured => _options.CurrentValue.Enabled && !string.IsNullOrWhiteSpace(_options.CurrentValue.Bucket);

    public async Task UploadAsync(string objectPath, byte[] data, string contentType, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new DocumentVaultStoreException("Document vault is not configured.");
        }

        var bucket = _options.CurrentValue.Bucket;
        var encodedName = EncodeObjectPath(objectPath);
        var uri = $"https://storage.googleapis.com/upload/storage/v1/b/{EncodeSegment(bucket)}/o?uploadType=media&name={encodedName}";

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Content = new ByteArrayContent(data);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var response = await SendAsync(request, ct);
        await EnsureSuccessAsync(response, "upload document", ct);
    }

    public async Task<byte[]?> DownloadAsync(string objectPath, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new DocumentVaultStoreException("Document vault is not configured.");
        }

        var bucket = _options.CurrentValue.Bucket;
        var encodedName = EncodeObjectPath(objectPath);
        var uri = $"https://storage.googleapis.com/storage/v1/b/{EncodeSegment(bucket)}/o/{encodedName}?alt=media";

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "download document", ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task DeleteIfExistsAsync(string objectPath, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new DocumentVaultStoreException("Document vault is not configured.");
        }

        var bucket = _options.CurrentValue.Bucket;
        var encodedName = EncodeObjectPath(objectPath);
        var uri = $"https://storage.googleapis.com/storage/v1/b/{EncodeSegment(bucket)}/o/{encodedName}";

        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        using var response = await SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, "delete document", ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string accessToken;
        try
        {
            accessToken = await GetAccessTokenAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not obtain Application Default Credentials for GCS.");
            throw new DocumentVaultStoreException("Authentication token acquisition failed.", ex);
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
            throw new DocumentVaultStoreException("GCS could not be reached.", exception);
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
        throw new DocumentVaultStoreException(
            $"GCS could not {action} (HTTP {(int)response.StatusCode}).");
    }

    /// <summary>
    /// Percent-encodes an object name for the GCS JSON API. The separators are encoded too
    /// (<c>a/b.pdf</c> becomes <c>a%2Fb.pdf</c>): in <c>/b/{bucket}/o/{object}</c> the name is a
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
