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
    public static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>("financialapp.http.request_duration_ms", "ms", "End-to-end API request duration");
    public static readonly Histogram<double> DatabaseDurationPerRequest = Meter.CreateHistogram<double>("financialapp.http.database_duration_ms", "ms", "Database duration accumulated within one API request");
    public static readonly Histogram<int> DatabaseCommandsPerRequest = Meter.CreateHistogram<int>("financialapp.http.database_commands", "{command}", "Database commands executed within one API request");
    public static readonly Histogram<double> DatabaseCommandDuration = Meter.CreateHistogram<double>("financialapp.database.command_duration_ms", "ms", "Individual database command duration");
    public static readonly Counter<long> SlowDatabaseCommands = Meter.CreateCounter<long>("financialapp.database.slow_commands", "{command}", "Database commands taking at least 250 ms");
}
