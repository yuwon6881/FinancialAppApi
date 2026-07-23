namespace FinancialAppApi.Services.Push;

public enum FcmSendStatus
{
    Sent,
    InvalidOrUnregistered,
    TransientFailure
}

public sealed record FcmSendResult(FcmSendStatus Status, string? ErrorDetail = null);

// A single push reminder's content. Deliberately carries only the payment name and a timing
// phrase (e.g. "Due today") plus a deep link — never an amount, category, or any other financial
// detail, since notification payloads can be visible on a locked screen or captured by the OS.
public sealed record PushNotificationContent(
    string Title,
    string Body,
    string Tag,
    string Route,
    string RecurringPaymentId,
    DateOnly OccurrenceDate,
    TimeSpan TimeToLive);

public interface IFcmPushSender
{
    Task<FcmSendResult> SendAsync(string fcmToken, PushNotificationContent content, CancellationToken cancellationToken = default);
}
