using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.SavingsGoals;

public partial class SavingsGoalService
{
    /// <summary>
    /// Eligible bucket money on hand right now: the balance carried into the current cycle plus this cycle's
    /// bucket ledger movement. Mirrors how the dashboard and the AI context loader derive it, so
    /// the number the invariant is checked against is the number the user sees.
    /// </summary>
    public async Task<decimal> GetBucketBalanceAsync(
        string fundingBucket,
        int cycleDay,
        CancellationToken cancellationToken = default)
    {
        EnsureFundingBucket(fundingBucket);
        var today = _financialClock.Today;
        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(today, cycleDay);
        var opening = await _cycleBalanceService.GetOpeningBalanceAsync(year, monthIndex, cycleDay);

        var (cycleStart, cycleEnd, _) = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);
        var startInclusive = TransactionDate.StartOfDate(DateOnly.FromDateTime(cycleStart));
        var endExclusive = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(cycleEnd));

        var cycleTxs = await _context.Transactions
            .AsNoTracking()
            .Where(t => t.Date >= startInclusive && t.Date < endExclusive)
            .ToListAsync(cancellationToken);

        var openingBalance = fundingBucket == SavingsGoalFundingBucket.Essentials
            ? opening.essentials
            : opening.rewards;
        return openingBalance + cycleTxs.Sum(t => CategoryAttributionService.GetCategoryAmount(t, fundingBucket));
    }

    private async Task<decimal> GetUnassignedAsync(
        string fundingBucket = SavingsGoalFundingBucket.Rewards,
        int? cycleDay = null,
        CancellationToken cancellationToken = default)
    {
        EnsureFundingBucket(fundingBucket);
        var resolvedCycleDay = cycleDay ?? await GetCycleDayAsync(cancellationToken);
        var bucketBalance = await GetBucketBalanceAsync(fundingBucket, resolvedCycleDay, cancellationToken);
        var pendingBucket = await GetPendingBucketRecurringAsync(fundingBucket, resolvedCycleDay, cancellationToken);
        var totalEarmarked = await _context.SavingsGoals
            .Where(goal => goal.Status == SavingsGoalStatus.Active && goal.FundingBucket == fundingBucket)
            .SumAsync(goal => goal.EarmarkedAmount, cancellationToken);
        return SavingsGoalPacing.Unassigned(bucketBalance, totalEarmarked, pendingBucket);
    }

    /// <summary>
    /// Holds an unsettled Rewards subscription out of the same pool that goals and wishlist claims
    /// use. Occurrence-tagged transactions are matched by their exact occurrence date; untagged
    /// transactions retain the legacy posting-date fallback because older ledger rows predate the
    /// occurrence column.
    /// </summary>
    private async Task<decimal> GetPendingBucketRecurringAsync(
        string fundingBucket,
        int cycleDay,
        CancellationToken cancellationToken)
    {
        EnsureFundingBucket(fundingBucket);
        var today = _financialClock.Today;
        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(today, cycleDay);
        var range = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);
        var startOnly = DateOnly.FromDateTime(range.start);
        var endOnly = DateOnly.FromDateTime(range.end);
        var payments = await _context.RecurringPayments
            .AsNoTracking()
            .Where(payment => payment.Active)
            .ToListAsync(cancellationToken);

        var occurrences = await _recurringOccurrenceLedger.GetRangeAsync(
            payments, startOnly, endOnly, cancellationToken: cancellationToken);
        var activeIds = payments.Select(payment => payment.Id).ToHashSet(StringComparer.Ordinal);
        var claimed = occurrences.Where(occurrence => activeIds.Contains(occurrence.RecurringPaymentId)).ToList();

        // Only a partially paid occurrence needs its ledger rows read; every other status either
        // claims its full scheduled amount or claims nothing.
        var partiallyPaid = claimed
            .Where(occurrence => occurrence.Status == RecurringOccurrenceStatus.PartiallyPaid)
            .ToList();
        Dictionary<(string PaymentId, DateOnly Date), decimal>? paidByOccurrence = null;
        if (partiallyPaid.Count > 0)
        {
            var partialPaymentIds = partiallyPaid.Select(o => o.RecurringPaymentId).Distinct(StringComparer.Ordinal).ToList();
            var partialDates = partiallyPaid.Select(o => o.OccurrenceDate).Distinct().ToList();
            var partialTransactions = await _context.Transactions
                .AsNoTracking()
                .Where(t => t.RecurringPaymentId != null
                    && partialPaymentIds.Contains(t.RecurringPaymentId)
                    && t.RecurringOccurrenceDate != null
                    && partialDates.Contains(t.RecurringOccurrenceDate.Value))
                .ToListAsync(cancellationToken);
            paidByOccurrence = RecurringOccurrenceAmounts.PaidByOccurrence(partialTransactions);
        }

        return SavingsGoalPacing.PendingAmount(claimed, fundingBucket, paidByOccurrence);
    }

    private async Task<int> GetCycleDayAsync(CancellationToken cancellationToken)
    {
        var setting = await _context.FinancialSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        return setting?.CycleDay ?? FinancialConstants.DefaultCycleDay;
    }

    private async Task<string> GetCurrentCycleKeyAsync(CancellationToken cancellationToken)
    {
        return CurrentCycleKey(_financialClock.Today, await GetCycleDayAsync(cancellationToken));
    }

    private static string CurrentCycleKey(DateOnly today, int cycleDay)
    {
        var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(today, cycleDay);
        return $"{year:D4}-{monthIndex:D2}";
    }

    private static int NormalizeRecurrenceDay(int? recurrenceDay, int fallbackDay)
    {
        return Math.Clamp(recurrenceDay ?? fallbackDay, 1, 31);
    }

    private static DateTime AddRecurringPeriod(DateTime currentDate, int recurrenceMonths, int recurrenceDay)
    {
        var nextMonth = currentDate.AddMonths(recurrenceMonths);
        var day = Math.Min(recurrenceDay, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
        return new DateTime(nextMonth.Year, nextMonth.Month, day);
    }

    private static SavingsGoalResult ExceedsAvailable(decimal headroom, string fundingBucket)
    {
        var bucketLabel = fundingBucket == SavingsGoalFundingBucket.Essentials ? "essentials" : "rewards";
        return new SavingsGoalResult(
            SavingsGoalMutationStatus.ExceedsAvailable,
            Message: $"That would claim more {bucketLabel} money than you have. Only {headroom:0.00} is unassigned.");
    }

    private static SavingsGoalResult? Validate(SavingsGoal goal)
    {
        if (string.IsNullOrWhiteSpace(goal.Name))
        {
            return new SavingsGoalResult(SavingsGoalMutationStatus.NameRequired, Message: "Goal name is required.");
        }
        if (goal.TargetAmount <= 0m)
        {
            return new SavingsGoalResult(SavingsGoalMutationStatus.TargetInvalid, Message: "Goal target must be greater than zero.");
        }
        if (goal.TargetDate == default)
        {
            return new SavingsGoalResult(SavingsGoalMutationStatus.TargetDateInvalid, Message: "Goal target date is required.");
        }
        if (!IsAllowedFundingBucket(goal.FundingBucket))
        {
            return new SavingsGoalResult(
                SavingsGoalMutationStatus.FundingBucketInvalid,
                Message: "Savings goals can use Essentials or Rewards only; Growth belongs to investments and Stability is reserved for emergency-fund recovery.");
        }
        if (goal.IsRecurring && (goal.RecurrenceMonths < 1 || goal.RecurrenceMonths > 120))
        {
            return new SavingsGoalResult(SavingsGoalMutationStatus.RecurrenceInvalid, Message: "Repeat interval must be between 1 and 120 months.");
        }
        if (SavingsGoalPacing.PriorityRank(goal.Priority) == 1 && goal.Priority is not ("Medium" or null))
        {
            // Unknown priority strings would silently rank as Medium; normalise instead of guessing.
            goal.Priority = "Medium";
        }

        return null;
    }

    private static bool IsAllowedFundingBucket(string? fundingBucket) =>
        fundingBucket is SavingsGoalFundingBucket.Essentials or SavingsGoalFundingBucket.Rewards;

    private static void EnsureFundingBucket(string fundingBucket)
    {
        if (!IsAllowedFundingBucket(fundingBucket))
        {
            throw new ArgumentException("Savings goals can use Essentials or Rewards only; Growth belongs to investments and Stability is reserved for emergency-fund recovery.", nameof(fundingBucket));
        }
    }
}
