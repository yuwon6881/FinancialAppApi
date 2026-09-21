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
        PushSubscriptionRetentionDays = Math.Clamp(
            configuration.GetValue("Retention:PushSubscriptionRetentionDays", 365), 30, 3650);
        CleanupInterval = TimeSpan.FromHours(Math.Clamp(
            configuration.GetValue("Retention:CleanupIntervalHours", 24), 1, 168));
    }

    /// <summary>Configuration-gated so a deployment can turn pruning off without a code change.</summary>
    public bool Enabled { get; }

    /// <summary>Six years by default: the five-year chart range plus a year of margin.</summary>
    public int PriceBarRetentionDays { get; }

    /// <summary>Applies to delivery receipts and spending-alert bookkeeping, not to any ledger row.</summary>
    public int NotificationRetentionDays { get; }

    /// <summary>
    /// How long a *disabled* push subscription row is kept after it was last touched. A year by
    /// default, comfortably past <see cref="NotificationRetentionDays"/>.
    /// <para>
    /// Disabled rows are kept at all because both delivery ledgers claim "already sent" against
    /// <c>PushSubscription.Id</c>, so deleting a row whose claims still exist would let an old
    /// reminder send twice. Once those claims have themselves aged out the id is unreferenced,
    /// and without this the table gained a permanent row every time a browser lost its stored
    /// device id -- clearing site data mints a new one, so the old row could never be reused.
    /// The prune is still guarded row by row rather than relying on this ordering, because both
    /// horizons are configurable independently.
    /// </para>
    /// </summary>
    public int PushSubscriptionRetentionDays { get; }

    public TimeSpan CleanupInterval { get; }
}
