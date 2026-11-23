using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Accounts;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Forecast;

/// <summary>A temporary what-if line. Bucket null means income that is split like a salary.</summary>
public sealed record CashFlowScenarioItem(DateOnly Date, decimal Amount, string? Bucket, string Label);

/// <summary>
/// Everything the user can change in the forecast workspace. Nothing here is stored: the scenario
/// is evaluated against the user's real balances, settings and bills and then discarded.
/// </summary>
public sealed record CashFlowScenario(
    int Cycles,
    decimal? IncomePerCycle,
    decimal? EverydaySpendingPerCycle,
    IReadOnlyList<CashFlowScenarioItem> Items,
    IReadOnlyCollection<string> SkippedBills);

public sealed record CashFlowForecastResponse(
    DateOnly Today,
    decimal StabilityTarget,
    CashFlowForecastResult Forecast,
    // Bills the scenario leaves out, so the workspace can name them and offer to bring them back.
    IReadOnlyList<ForecastItem> SkippedBills);

/// <summary>
/// Gathers the real inputs (bucket balances, allocation settings, upcoming bills) and hands them
/// with the scenario to <see cref="CashFlowForecastCalculator"/>. Read-only: it never writes.
/// </summary>
public sealed class CashFlowForecastService(
    AppDbContext context,
    LedgerAccountService accounts,
    RecurringOccurrenceService occurrences,
    FinancialClock clock)
{
    public const int MinCycles = 1;
    public const int MaxCycles = 12;
    public const int MaxItems = 50;
    public const int MaxSkippedBills = 200;
    public const decimal MaxAmount = 100_000_000m;

    public static string BillKey(string paymentId, DateOnly date) => $"{paymentId}:{date:yyyy-MM-dd}";

    /// <summary>Returns an error message when the scenario is out of range, otherwise null.</summary>
    public static string? Validate(CashFlowScenario scenario)
    {
        if (scenario.Cycles is < MinCycles or > MaxCycles)
            return $"Choose between {MinCycles} and {MaxCycles} cycles.";
        if (scenario.IncomePerCycle is < 0m or > MaxAmount)
            return "Income must be zero or more.";
        if (scenario.EverydaySpendingPerCycle is < 0m or > MaxAmount)
            return "Everyday spending must be zero or more.";
        if (scenario.Items.Count > MaxItems)
            return $"Add at most {MaxItems} what-if items.";
        if (scenario.SkippedBills.Count > MaxSkippedBills)
            return "Too many skipped bills.";
        foreach (var item in scenario.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Label) || item.Label.Trim().Length > 80)
                return "Each what-if item needs a name of up to 80 characters.";
            if (item.Amount == 0m || Math.Abs(item.Amount) > MaxAmount)
                return "Each what-if item needs a non-zero amount.";
            if (item.Bucket is null && item.Amount < 0m)
                return "Extra income must be a positive amount.";
            if (item.Bucket is not null && ForecastBuckets.Normalize(item.Bucket) is null)
                return "Choose Essentials, Growth, Stability or Rewards.";
        }
        return null;
    }

    public async Task<CashFlowForecastResponse> ForecastAsync(CashFlowScenario scenario, CancellationToken ct)
    {
        var today = clock.Today;
        var setting = await context.FinancialSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new FinancialSetting();
        var periods = BuildPeriods(today, setting.CycleDay, scenario.Cycles);
        var horizonEnd = periods[^1].End;

        var ledgerAccounts = await accounts.GetAccountsAsync(ct);
        var balances = ledgerAccounts.Count == 0
            ? new Dictionary<string, decimal>()
            : await accounts.GetBalancesAsync(ledgerAccounts, ct);
        var opening = ForecastBuckets.All.ToDictionary(
            bucket => bucket,
            bucket => ledgerAccounts
                .Where(account => string.Equals(account.Bucket, bucket, StringComparison.OrdinalIgnoreCase))
                .Sum(account => balances.GetValueOrDefault(account.Id)));

        var bills = await LoadBillsAsync(ledgerAccounts, today, horizonEnd, setting.CycleDay, ct);
        var skipped = scenario.SkippedBills.ToHashSet(StringComparer.Ordinal);
        var skippedBills = bills.Where(bill => bill.SourceId is not null && skipped.Contains(bill.SourceId)).ToList();
        var items = bills
            .Except(skippedBills)
            .Concat(scenario.Items.Select(item => new ForecastItem(
                item.Date, item.Amount, ForecastBuckets.Normalize(item.Bucket), item.Label.Trim(), ForecastItemKinds.Scenario)))
            .ToList();

        var result = CashFlowForecastCalculator.Project(new CashFlowForecastInput(
            periods,
            opening,
            new ForecastAllocations(setting.EssentialsAlloc, setting.GrowthAlloc, setting.StabilityAlloc, setting.RewardsAlloc),
            setting.TargetStabilityFund,
            setting.StabilityOverflowRedirect,
            scenario.IncomePerCycle,
            scenario.EverydaySpendingPerCycle,
            items));
        return new CashFlowForecastResponse(today, setting.TargetStabilityFund, result, skippedBills);
    }

    /// <summary>The rest of the current cycle (from today) followed by <paramref name="cycles"/> whole cycles.</summary>
    public static IReadOnlyList<ForecastPeriod> BuildPeriods(DateOnly today, int cycleDay, int cycles)
    {
        var current = AI.AiCycleResolver.Current(today, cycleDay);
        var currentRange = AI.AiCycleResolver.Range(current, cycleDay);
        var periods = new List<ForecastPeriod>
        {
            new(today, currentRange.LastDate, "Rest of this cycle", true)
        };
        for (var offset = 1; offset <= cycles; offset++)
        {
            var cycle = current.AddCycles(offset);
            var range = AI.AiCycleResolver.Range(cycle, cycleDay);
            periods.Add(new ForecastPeriod(range.FirstDate, range.LastDate, AI.AiCycleResolver.Label(cycle, cycleDay), false));
        }
        return periods;
    }

    /// <summary>
    /// Every bill still to be paid between today and the horizon. Tracked occurrences supply what
    /// is still owed (a partial owes only its remainder; a resolved one owes nothing, and an
    /// overdue unresolved occurrence remains due across cycle boundaries). Untracked future dates come from the
    /// schedule at the bill's current amount. Paused bills are left out, as in committed totals.
    /// </summary>
    private async Task<List<ForecastItem>> LoadBillsAsync(
        IReadOnlyList<LedgerAccount> ledgerAccounts,
        DateOnly today,
        DateOnly horizonEnd,
        int cycleDay,
        CancellationToken ct)
    {
        var payments = await context.RecurringPayments.AsNoTracking().Where(payment => payment.Active).ToListAsync(ct);
        if (payments.Count == 0) return [];

        var activeIds = payments.Select(payment => payment.Id).ToArray();
        var trackedQuery = context.RecurringPaymentOccurrences.AsNoTracking()
            .Where(occurrence => activeIds.Contains(occurrence.RecurringPaymentId)
                && occurrence.OccurrenceDate <= horizonEnd);
        // Older resolved rows can't affect the projection. Query overdue rows by unresolved status.
        var tracked = await trackedQuery.Where(occurrence => occurrence.OccurrenceDate >= today
            || occurrence.Status == RecurringOccurrenceStatus.Pending
            || occurrence.Status == RecurringOccurrenceStatus.PartiallyPaid).ToListAsync(ct);
        var paidRows = !tracked.Any(occurrence => occurrence.Status == RecurringOccurrenceStatus.PartiallyPaid)
            ? []
            : await (
                from occurrence in trackedQuery
                where occurrence.Status == RecurringOccurrenceStatus.PartiallyPaid
                join transaction in context.Transactions.AsNoTracking()
                    on new { PaymentId = occurrence.RecurringPaymentId, Date = (DateOnly?)occurrence.OccurrenceDate }
                    equals new { PaymentId = transaction.RecurringPaymentId, Date = transaction.RecurringOccurrenceDate }
                select transaction).ToListAsync(ct);
        var paidByOccurrence = RecurringOccurrenceAmounts.PaidByOccurrence(paidRows);

        var bucketByAccount = ledgerAccounts.ToDictionary(account => account.Id, account => account.Bucket, StringComparer.Ordinal);
        var trackedByKey = tracked
            .GroupBy(occurrence => (occurrence.RecurringPaymentId, occurrence.OccurrenceDate))
            .ToDictionary(group => group.Key, group => group.First());
        var trackedDates = tracked.ToLookup(occurrence => occurrence.RecurringPaymentId, occurrence => occurrence.OccurrenceDate);

        var items = new List<ForecastItem>();
        foreach (var payment in payments)
        {
            var dates = occurrences
                .GetOccurrencesInRange(payment, today.ToDateTime(TimeOnly.MinValue), horizonEnd.ToDateTime(TimeOnly.MinValue), cycleDay)
                .Select(DateOnly.FromDateTime)
                .Concat(trackedDates[payment.Id])
                .Distinct()
                .OrderBy(date => date);
            foreach (var date in dates)
            {
                decimal owed;
                string? accountId = payment.AccountId;
                if (trackedByKey.TryGetValue((payment.Id, date), out var occurrence))
                {
                    owed = RecurringOccurrenceAmounts.Outstanding(occurrence, payment.Amount, paidByOccurrence);
                    accountId = RecurringOccurrenceAccounts.Scheduled(occurrence.AccountId, payment.AccountId);
                }
                else
                {
                    owed = Math.Abs(payment.Amount);
                }
                if (owed <= 0m) continue;

                var bucket = accountId is not null && bucketByAccount.TryGetValue(accountId, out var accountBucket)
                    ? ForecastBuckets.Normalize(accountBucket)
                    : ForecastBuckets.Normalize(payment.LedgerCategory);
                if (bucket is null) continue;

                // An overdue bill is still owed now, so it lands on the first forecast day.
                items.Add(new ForecastItem(
                    date < today ? today : date,
                    -owed,
                    bucket,
                    payment.Name,
                    ForecastItemKinds.Bill,
                    BillKey(payment.Id, date)));
            }
        }
        return items;
    }
}
