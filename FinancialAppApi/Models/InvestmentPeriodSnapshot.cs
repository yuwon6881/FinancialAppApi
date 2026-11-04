using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public static class InvestmentSnapshotSegments
{
    public const string Total = "Total";
    public const string Unassigned = "Unassigned";

    /// <summary>The segment an instrument's history is grouped under: its plan sleeve, or Unassigned.</summary>
    public static string For(InvestmentInstrument instrument)
        => string.IsNullOrWhiteSpace(instrument.AllocationSleeve) ? Unassigned : instrument.AllocationSleeve;
}

/// <summary>
/// One settled month-end of the investment portfolio, for the whole portfolio (Total) or one
/// market segment. A derived cache, never an authority: every figure is reproducible from the
/// activity, cash movements, and stored market data, and any write that could change a month
/// deletes it and everything after it. Readers resume from the newest row instead of replaying
/// decades of history.
///
/// Values are in the app currency, each flow converted at its own date's rate, so a currency
/// switch invalidates every row. Monthly figures (contributions, dividends, realised) are flows
/// within the month; CostBasis and the values are levels at the month-end. Unknown stays null.
/// </summary>
public sealed class InvestmentPeriodSnapshot : IUserOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public string UserId { get; set; } = string.Empty;
    public DateOnly PeriodEnd { get; set; }
    [Required, MaxLength(32)] public string Segment { get; set; } = InvestmentSnapshotSegments.Total;
    [Required, MaxLength(3)] public string AppCurrency { get; set; } = "USD";
    // The market-data provider whose prices and rates valued this month; a switch rebuilds it.
    [Required, MaxLength(32)] public string Provider { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }

    public decimal? OpeningValue { get; set; }
    public decimal? ClosingValue { get; set; }
    // Total only: what the holdings and the uninvested broker cash were each worth.
    public decimal? MarketValue { get; set; }
    public decimal? CashValue { get; set; }

    // Total: deposits less withdrawals. A segment: money that moved into it through buys, less
    // sale proceeds -- cash is held per account, not per market, so trades are a segment's flows.
    public decimal? NetContributions { get; set; }
    // Each flow weighted by the share of the month it was invested for (Modified Dietz), so a
    // month's time-weighted return is computable without re-reading the flows.
    public decimal? WeightedContributions { get; set; }
    public decimal? Dividends { get; set; }
    public decimal? RealisedProfitLoss { get; set; }
    public decimal? CostBasis { get; set; }

    public bool IsComplete { get; set; }
    [MaxLength(500)] public string? MissingReason { get; set; }
    // Total only: positions and cash per account and currency at PeriodEnd, so the next month
    // replays from here. An unreadable value makes the row unusable as a resume point.
    public string? CarryState { get; set; }
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;
}
