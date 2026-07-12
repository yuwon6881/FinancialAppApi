using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FinancialAppApi.Diagnostics;

public static class Telemetry
{
    public const string ServiceName = "FinancialAppApi";
    public static readonly ActivitySource ActivitySource = new(ServiceName);
    public static readonly Meter Meter = new(ServiceName);

    // Custom Counters / Histograms
    public static readonly Counter<long> AiActionsCounter = Meter.CreateCounter<long>("financialapp.ai_actions.count", "Count of AI assistant requests");
    public static readonly Histogram<double> DashboardLoadDuration = Meter.CreateHistogram<double>("financialapp.dashboard.load_duration_ms", "ms", "Dashboard load duration");
    public static readonly Counter<long> CacheHitsCounter = Meter.CreateCounter<long>("financialapp.cache.hits", "Count of cache hits");
    public static readonly Counter<long> CacheMissesCounter = Meter.CreateCounter<long>("financialapp.cache.misses", "Count of cache misses");
}
