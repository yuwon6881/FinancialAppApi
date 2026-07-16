namespace FinancialAppApi.Services;

/// <summary>
/// Bounds how much transient OCR state can accumulate in Postgres. Completed results
/// remain available long enough for idempotent client polling, while abandoned jobs
/// eventually release their (much larger) image payloads.
/// </summary>
public sealed class ReceiptScanRetentionPolicy
{
    public ReceiptScanRetentionPolicy(IConfiguration configuration)
    {
        TerminalJobRetention = TimeSpan.FromHours(Math.Clamp(
            configuration.GetValue("Ocr:TerminalJobRetentionHours", 24), 1, 168));
        AbandonedJobRetention = TimeSpan.FromHours(Math.Clamp(
            configuration.GetValue("Ocr:AbandonedJobRetentionHours", 24), 1, 168));
        CleanupInterval = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("Ocr:CleanupIntervalMinutes", 60), 5, 1440));
        MaxOutstandingJobsPerUser = Math.Clamp(
            configuration.GetValue("Ocr:MaxOutstandingJobsPerUser", 5), 1, 20);
    }

    public TimeSpan TerminalJobRetention { get; }

    public TimeSpan AbandonedJobRetention { get; }

    public TimeSpan CleanupInterval { get; }

    public int MaxOutstandingJobsPerUser { get; }
}
