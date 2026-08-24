namespace FinancialAppApi.Services;

/// <summary>
/// Bounds the append-only tables that would otherwise grow for the life of the account.
/// <para>
/// Deliberately narrow. Vault documents are excluded because their retention is advisory by design
/// (there is no automatic purge), and FX rate bars are excluded because historical cost basis is
/// valued at each trade's own date -- a trade from eight years ago still needs the rate from eight
/// years ago, so pruning them would quietly turn a known cost basis into an unknown one. Price bars
/// have no such requirement: they are read for current valuation and for charts, and the longest
/// chart range the app offers is five years.
/// </para>
/// </summary>
public sealed class LedgerRetentionPolicy
{
    public LedgerRetentionPolicy(IConfiguration configuration)
    {
        Enabled = configuration.GetValue("Retention:Enabled", true);
        PriceBarRetentionDays = Math.Clamp(
            configuration.GetValue("Retention:PriceBarRetentionDays", 366 * 6), 366, 366 * 25);
        NotificationRetentionDays = Math.Clamp(
            configuration.GetValue("Retention:NotificationRetentionDays", 180), 30, 3650);
        CleanupInterval = TimeSpan.FromHours(Math.Clamp(
            configuration.GetValue("Retention:CleanupIntervalHours", 24), 1, 168));
    }

    /// <summary>Configuration-gated so a deployment can turn pruning off without a code change.</summary>
    public bool Enabled { get; }

    /// <summary>Six years by default: the five-year chart range plus a year of margin.</summary>
    public int PriceBarRetentionDays { get; }

    /// <summary>Applies to delivery receipts and spending-alert bookkeeping, not to any ledger row.</summary>
    public int NotificationRetentionDays { get; }

    public TimeSpan CleanupInterval { get; }
}
