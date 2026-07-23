using System.Net;
using System.Net.Http.Headers;
using Google.Apis.Auth.OAuth2;

namespace FinancialAppApi.Services.Push;

// Sends via the FCM HTTP v1 REST API using Application Default Credentials for the OAuth2
// access token, rather than a legacy server key. This is the credential path Cloud Run already
// gets for free from its attached service account, so no secret ever needs to be stored.
public sealed class FcmHttpV1PushSender : IFcmPushSender
{
    private const string MessagingScope = "https://www.googleapis.com/auth/firebase.messaging";

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FcmHttpV1PushSender> _logger;
    private GoogleCredential? _scopedCredential;

    public FcmHttpV1PushSender(HttpClient httpClient, IConfiguration configuration, ILogger<FcmHttpV1PushSender> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<FcmSendResult> SendAsync(string fcmToken, PushNotificationContent content, CancellationToken cancellationToken = default)
    {
        var projectId = _configuration["Fcm:ProjectId"];
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return new FcmSendResult(FcmSendStatus.TransientFailure, "Fcm:ProjectId is not configured.");
        }

        string accessToken;
        try
        {
            accessToken = await GetAccessTokenAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not obtain Application Default Credentials for FCM.");
            return new FcmSendResult(FcmSendStatus.TransientFailure, "ADC token acquisition failed.");
        }

        var payload = new
        {
            message = new
            {
                token = fcmToken,
                webpush = new
                {
                    headers = new Dictionary<string, string>
                    {
                        ["TTL"] = Math.Max(1, (long)content.TimeToLive.TotalSeconds).ToString()
                    }
                },
                data = new Dictionary<string, string>
                {
                    ["title"] = content.Title,
                    ["body"] = content.Body,
                    ["tag"] = content.Tag,
                    ["route"] = content.Route,
                    ["recurringPaymentId"] = content.RecurringPaymentId,
                    ["occurrenceDate"] = content.OccurrenceDate.ToString("yyyy-MM-dd")
                }
            }
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://fcm.googleapis.com/v1/projects/{projectId}/messages:send")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;
        try
        {
            // Never log the token or the notification content here — only the outcome. A
            // network-level failure (timeout, DNS, connection reset) must fail this single
            // device closed rather than throw and take the whole dispatch run down with it.
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "FCM send threw before a response was received.");
            return new FcmSendResult(FcmSendStatus.TransientFailure, "FCM request failed before a response was received.");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return new FcmSendResult(FcmSendStatus.Sent);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound ||
                body.Contains("UNREGISTERED", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("INVALID_ARGUMENT", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("SENDER_ID_MISMATCH", StringComparison.OrdinalIgnoreCase))
            {
                return new FcmSendResult(FcmSendStatus.InvalidOrUnregistered);
            }

            _logger.LogWarning("FCM send failed with status {StatusCode}.", response.StatusCode);
            return new FcmSendResult(FcmSendStatus.TransientFailure, $"FCM responded {(int)response.StatusCode}.");
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        _scopedCredential ??= await BuildScopedCredentialAsync(cancellationToken);
        return await _scopedCredential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
    }

    private static async Task<GoogleCredential> BuildScopedCredentialAsync(CancellationToken cancellationToken)
    {
        var credential = await GoogleCredential.GetApplicationDefaultAsync(cancellationToken);
        return credential.IsCreateScopedRequired ? credential.CreateScoped(MessagingScope) : credential;
    }
}
