namespace FinancialAppApi.Models;

// Persisted ending balance of each of the 4 budget buckets for one closed cycle, keyed by the
// calendar (Year, MonthIndex) pair used by CategoryAttributionService.GetCycleRange -- NOT the shifted
// cycle label. Balances are cumulative/carry-forward across cycles (see FinancialController),
// so a row's values already include every prior cycle's net change back to the app baseline.
// Rows are only ever appended or deleted in bulk (CycleBalanceService); never updated in place.
public class CycleBalance : IUserOwnedEntity
{
    public string UserId { get; set; } = string.Empty;

    public int Year { get; set; }

    // 1-12, the calendar month that seeds GetCycleRange(Year, MonthIndex, cycleDay).
    public int MonthIndex { get; set; }

    public decimal EssentialsBalance { get; set; }
    public decimal GrowthBalance { get; set; }
    public decimal StabilityBalance { get; set; }
    public decimal StabilityReloadOutstanding { get; set; }
    public DateOnly? StabilityReloadOldestDate { get; set; }

    // The reload queue's still-owing entries, serialized by StabilityReloadObligationCache. The two
    // aggregate columns above cannot say which drawdown a carried obligation came from, so without
    // this the next cycle replays against one anonymous entry and every reported total counts
    // drawdowns that were already put back in full.
    public string? StabilityReloadObligations { get; set; }
    public decimal RewardsBalance { get; set; }
}
