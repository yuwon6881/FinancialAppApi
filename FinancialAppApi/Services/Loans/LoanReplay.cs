using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Loans;

public sealed record LoanReplayResult(
    decimal OutstandingBalance,
    decimal ScheduledPayment,
    decimal TotalScheduledInterest,
    decimal TotalInterestPaid,
    DateOnly? PayoffDate,
    DateOnly? LastOccurrenceDate,
    IReadOnlyList<LoanPaymentSplit> Payments,
    IReadOnlyList<LoanScheduleEntry> FutureSchedule);

/// <summary>
/// Replays the linked settlement history. Occurrence date is the amortization order; Transaction
/// Date is deliberately ignored because pay-early posts today while retaining its scheduled date.
/// </summary>
public static class LoanReplay
{
    public static LoanReplayResult Replay(
        Loan loan,
        IEnumerable<LoanPaymentInput> inputs) =>
        ReplayCore(
            loan,
            loan.ScheduleFrequency,
            inputs,
            loan.ScheduleDueDay,
            loan.ScheduleStartDate,
            scheduleAvailable: loan.ScheduleStatus != LoanScheduleStatus.Incomplete);

    // Kept as an explicit-cadence overload for pure arithmetic callers and compatibility with
    // older tests. Production read models use the immutable cadence stored on Loan above.
    public static LoanReplayResult Replay(
        Loan loan,
        string? frequency,
        IEnumerable<LoanPaymentInput> inputs,
        int? dueDay = null)
    {
        var resolvedFrequency = frequency ?? loan.ScheduleFrequency;
        var resolvedDueDay = dueDay ?? loan.ScheduleDueDay ?? loan.TrackingStartDate.Day;
        var scheduleStartDate = loan.ScheduleStartDate ?? loan.TrackingStartDate;
        return ReplayCore(
            loan,
            resolvedFrequency,
            inputs,
            resolvedDueDay,
            scheduleStartDate,
            scheduleAvailable: frequency != null || dueDay != null
                ? HasValidCadence(resolvedFrequency, resolvedDueDay, scheduleStartDate)
                : loan.ScheduleStatus != LoanScheduleStatus.Incomplete
                    && HasValidCadence(resolvedFrequency, resolvedDueDay, scheduleStartDate));
    }

    private static LoanReplayResult ReplayCore(
        Loan loan,
        string? frequency,
        IEnumerable<LoanPaymentInput> inputs,
        int? dueDay,
        DateOnly? scheduleStartDate,
        bool scheduleAvailable)
    {
        if (!scheduleAvailable || !HasValidCadence(frequency, dueDay, scheduleStartDate))
        {
            return new LoanReplayResult(
                0m,
                0m,
                0m,
                0m,
                null,
                null,
                [],
                []);
        }

        var ordered = inputs
            .Where(input => input.OccurrenceDate >= loan.TrackingStartDate)
            .OrderBy(input => input.OccurrenceDate)
            .ThenBy(input => input.PostedAt)
            .ThenBy(input => input.TransactionId, StringComparer.Ordinal)
            .ToList();

        var balance = LoanAmortization.RoundMoney(Math.Max(0m, loan.OpeningPrincipal));
        var flatInterestPaid = 0m;
        var paymentNumber = 0;
        var totalInterestPaid = 0m;
        DateOnly? lastOccurrenceDate = null;
        DateOnly? payoffDate = null;
        var payments = new List<LoanPaymentSplit>();

        foreach (var input in ordered)
        {
            if (!lastOccurrenceDate.HasValue || input.OccurrenceDate > lastOccurrenceDate.Value)
            {
                lastOccurrenceDate = input.OccurrenceDate;
            }
            if (input.IsDiscarded) continue;

            paymentNumber++;
            var split = LoanAmortization.ApplyPayment(
                loan,
                frequency,
                input.OccurrenceDate,
                balance,
                Math.Abs(input.Amount),
                paymentNumber,
                flatInterestPaid,
                input.TransactionId);
            payments.Add(split);
            if (split.BalanceBefore > 0m
                && split.BalanceAfter <= 0m)
            {
                payoffDate ??= input.OccurrenceDate;
            }
            balance = split.BalanceAfter;
            flatInterestPaid = LoanAmortization.RoundMoney(flatInterestPaid + split.Interest);
            totalInterestPaid = LoanAmortization.RoundMoney(totalInterestPaid + split.Interest);
        }

        var scheduledPayment = LoanAmortization.ScheduledPayment(loan, frequency);
        var totalScheduledInterest = LoanAmortization.TotalScheduledInterestForFrequency(loan, frequency);
        var outstandingBalance = balance;
        var futureSchedule = new List<LoanScheduleEntry>();
        var nextDate = lastOccurrenceDate.HasValue
            ? AddPeriod(lastOccurrenceDate.Value, frequency, dueDay)
            : FindOccurrenceOnOrAfter(scheduleStartDate!.Value, dueDay!.Value, frequency!, loan.TrackingStartDate);

        for (var i = 0; i < 600 && balance > 0m; i++)
        {
            var futureNumber = paymentNumber + i + 1;
            var entry = LoanAmortization.ApplyScheduledPayment(
                loan,
                frequency,
                nextDate,
                balance,
                futureNumber,
                flatInterestPaid);
            futureSchedule.Add(entry);
            balance = entry.BalanceAfter;
            flatInterestPaid = LoanAmortization.RoundMoney(flatInterestPaid + entry.Interest);
            if (balance <= 0m)
            {
                payoffDate ??= nextDate;
                break;
            }
            nextDate = AddPeriod(nextDate, frequency, dueDay);
        }

        return new LoanReplayResult(
            outstandingBalance,
            scheduledPayment,
            totalScheduledInterest,
            totalInterestPaid,
            balance > 0m ? null : payoffDate,
            lastOccurrenceDate,
            payments,
            futureSchedule);
    }

    private static bool HasValidCadence(string? frequency, int? dueDay, DateOnly? scheduleStartDate) =>
        scheduleStartDate.HasValue
        && dueDay is >= 1 and <= 31
        && (string.Equals(frequency, "Monthly", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frequency, "Annually", StringComparison.OrdinalIgnoreCase));

    private static DateOnly FindOccurrenceOnOrAfter(
        DateOnly start,
        int dueDay,
        string frequency,
        DateOnly date)
    {
        var annual = string.Equals(frequency, "Annually", StringComparison.OrdinalIgnoreCase);
        if (annual)
        {
            var year = Math.Max(start.Year, date.Year);
            var candidate = AnchoredDate(year, start.Month, dueDay);
            if (candidate < start || candidate < date)
                candidate = AnchoredDate(year + 1, start.Month, dueDay);
            return candidate;
        }

        var candidateYear = Math.Max(start.Year, date.Year);
        var candidateMonth = candidateYear == start.Year
            ? Math.Max(start.Month, date.Month)
            : date.Month;
        var monthlyCandidate = AnchoredDate(candidateYear, candidateMonth, dueDay);
        if (monthlyCandidate < start || monthlyCandidate < date)
        {
            candidateMonth++;
            if (candidateMonth == 13)
            {
                candidateMonth = 1;
                candidateYear++;
            }
            monthlyCandidate = AnchoredDate(candidateYear, candidateMonth, dueDay);
        }
        return monthlyCandidate;
    }

    private static DateOnly AnchoredDate(int year, int month, int dueDay) =>
        new(year, month, Math.Min(dueDay, DateTime.DaysInMonth(year, month)));

    private static DateOnly AddPeriod(DateOnly date, string? frequency, int? dueDay)
    {
        var anchorDay = dueDay is > 0 ? Math.Min(dueDay.Value, 31) : date.Day;
        var target = string.Equals(frequency, "Annually", StringComparison.OrdinalIgnoreCase)
            ? date.AddYears(1)
            : date.AddMonths(1);
        return new DateOnly(
            target.Year,
            target.Month,
            Math.Min(anchorDay, DateTime.DaysInMonth(target.Year, target.Month)));
    }
}
