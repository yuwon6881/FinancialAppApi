using FinancialAppApi.Services.Push;

namespace FinancialAppApi.Tests;

public class FcmHttpV1PushSenderTests
{
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
