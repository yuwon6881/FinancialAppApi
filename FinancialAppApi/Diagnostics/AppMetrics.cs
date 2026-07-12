using System.Diagnostics.Metrics;

namespace FinancialAppApi.Diagnostics;

public static class AppMetrics
{
    public static readonly Meter Meter = new("FinancialAppApi");

    public static readonly Histogram<double> DatabaseLatency = 
        Meter.CreateHistogram<double>("db.latency", unit: "ms", description: "Database latency in milliseconds");

    public static readonly Histogram<double> AiLatency = 
        Meter.CreateHistogram<double>("ai.latency", unit: "ms", description: "AI request latency in milliseconds");

    public static readonly Counter<int> AiTokenUsage = 
        Meter.CreateCounter<int>("ai.tokens", unit: "{token}", description: "AI token usage");

    public static readonly UpDownCounter<int> OcrQueueDepth = 
        Meter.CreateUpDownCounter<int>("ocr.queue.depth", description: "Current OCR queue depth");

    public static readonly Counter<int> OutboxFailures = 
        Meter.CreateCounter<int>("outbox.failures", description: "Number of outbox processing failures");

    public static readonly Counter<int> RateLimitRejections = 
        Meter.CreateCounter<int>("ratelimit.rejections", description: "Number of rate limit rejections");

    public static readonly Counter<int> AuthenticationFailures = 
        Meter.CreateCounter<int>("auth.failures", description: "Number of authentication failures");
}
