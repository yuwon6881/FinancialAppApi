using FinancialAppApi.Services.Push;
using FinancialAppApi.Models;
using System.Text.Json;

namespace FinancialAppApi.Tests;

public class FcmHttpV1PushSenderTests
{
    [Fact]
    public void BuildMessage_AndroidAddsVisibleNotificationDataAndDeliveryControls()
    {
        using var document = BuildDocument(PushPlatform.Android);
        var message = document.RootElement.GetProperty("message");
        var notification = message.GetProperty("android").GetProperty("notification");

        Assert.Equal("FinancialApp", notification.GetProperty("title").GetString());
        Assert.Equal("A bill reminder is ready.", notification.GetProperty("body").GetString());
        Assert.Equal("financialapp-alerts", notification.GetProperty("channelId").GetString());
        Assert.Equal("PUBLIC", notification.GetProperty("visibility").GetString());
        Assert.Equal("HIGH", message.GetProperty("android").GetProperty("priority").GetString());
        Assert.Equal("3600s", message.GetProperty("android").GetProperty("ttl").GetString());
        Assert.Equal("recurring-payment", message.GetProperty("data").GetProperty("kind").GetString());
        Assert.Equal("/recurring/rp-1", message.GetProperty("data").GetProperty("route").GetString());
    }

    [Fact]
    public void BuildMessage_IosUsesApnsAlertAndPreservesDataRoute()
    {
        using var document = BuildDocument(PushPlatform.Ios);
        var message = document.RootElement.GetProperty("message");
        var apns = message.GetProperty("apns");
        var headers = apns.GetProperty("headers");
        var payload = apns.GetProperty("payload").GetProperty("aps");

        Assert.Equal("alert", headers.GetProperty("apns-push-type").GetString());
        Assert.Equal("10", headers.GetProperty("apns-priority").GetString());
        Assert.Equal("FinancialApp", payload.GetProperty("alert").GetProperty("title").GetString());
        Assert.Equal("reminder-1", payload.GetProperty("thread-id").GetString());
        Assert.Equal("/recurring/rp-1", message.GetProperty("data").GetProperty("route").GetString());
    }

    [Fact]
    public void BuildMessage_WebKeepsTheExistingDataPayloadAndTtl()
    {
        using var document = BuildDocument(PushPlatform.Web);
        var message = document.RootElement.GetProperty("message");

        Assert.Equal("3600", message.GetProperty("webpush").GetProperty("headers").GetProperty("TTL").GetString());
        Assert.Equal("A bill reminder is ready.", message.GetProperty("data").GetProperty("body").GetString());
        Assert.False(message.TryGetProperty("android", out _));
        Assert.False(message.TryGetProperty("apns", out _));
    }

    private static JsonDocument BuildDocument(string platform)
    {
        var content = new PushNotificationContent(
            "recurring-payment",
            "FinancialApp",
            "A bill reminder is ready.",
            "reminder-1",
            "/recurring/rp-1",
            TimeSpan.FromHours(1),
            new Dictionary<string, string> { ["recurringPaymentId"] = "rp-1" },
            platform);
        var message = FcmHttpV1PushSender.BuildMessage(
            "test-token",
            content,
            new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        return JsonDocument.Parse(JsonSerializer.Serialize(new { message }));
    }

    [Theory]
    [InlineData("UNREGISTERED")]
    [InlineData("INVALID_ARGUMENT")]
    [InlineData("SENDER_ID_MISMATCH")]
    public void IsInvalidOrUnregistered_RecognizesStructuredDeviceErrors(string errorCode)
    {
        var body = $$"""
            {
              "error": {
                "status": "INVALID_ARGUMENT",
                "details": [{
                  "@type": "type.googleapis.com/google.firebase.fcm.v1.FcmError",
                  "errorCode": "{{errorCode}}"
                }]
              }
            }
            """;

        Assert.True(FcmHttpV1PushSender.IsInvalidOrUnregistered(body));
    }

    [Theory]
    [InlineData("""{"error":{"status":"NOT_FOUND","message":"Project was not found."}}""")]
    [InlineData("""{"error":{"status":"INVALID_ARGUMENT","message":"Payload is invalid."}}""")]
    [InlineData("<html>proxy error</html>")]
    public void IsInvalidOrUnregistered_DoesNotDisableADeviceForGenericProviderErrors(string body)
    {
        Assert.False(FcmHttpV1PushSender.IsInvalidOrUnregistered(body));
    }
}
