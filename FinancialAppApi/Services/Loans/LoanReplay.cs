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

        var scheduledPayment = LoanAmortization.ScheduledPayment(loan, frequency);
        var totalScheduledInterest = LoanAmortization.TotalScheduledInterestForFrequency(loan, frequency);

        var balance = LoanAmortization.RoundMoney(Math.Max(0m, loan.OpeningPrincipal));
        var flatInterestPaid = 0m;
        var paymentNumber = 0;
        var totalInterestPaid = 0m;
        var accrualDate = loan.TrackingStartDate;
        DateOnly? lastOccurrenceDate = null;
        DateOnly? payoffDate = null;
        var payments = new List<LoanPaymentSplit>();
        var lastOccurrenceComplete = true;
        var lastOccurrenceRemainder = 0m;
        var lastOccurrenceRemainingInterest = 0m;

        var occurrenceGroups = ordered
            .GroupBy(input => input.OccurrenceDate)
            .ToList();

        foreach (var group in occurrenceGroups)
        {
            var occurrenceDate = group.Key;
            lastOccurrenceDate = occurrenceDate;

            var isDiscarded = group.Any(input => input.IsDiscarded);
            if (isDiscarded)
            {
                // Discarded occurrence skips payment but moves the schedule forward.
                // Accrual date remains unchanged so days carry over into next daily rest window.
                lastOccurrenceComplete = true;
                continue;
            }

            var groupInputs = group.Where(input => !input.IsDiscarded).ToList();
            if (groupInputs.Count == 0) continue;

            var periodInterestDue = loan.InterestMethod switch
            {
                LoanInterestMethod.Flat => LoanAmortization.FlatInterestForPayment(loan, frequency, paymentNumber + 1, flatInterestPaid),
                LoanInterestMethod.ReducingBalanceDaily => LoanAmortization.DailyInterest(loan, balance, accrualDate, occurrenceDate),
                _ => LoanAmortization.RoundMoney(balance * LoanAmortization.AnnualRate(loan.AnnualRatePercent) / LoanAmortization.PeriodsPerYear(frequency))
            };

            var remainingInterestForPeriod = periodInterestDue;
            var totalPaidForPeriod = 0m;

            foreach (var input in groupInputs)
            {
                var split = LoanAmortization.ApplyPayment(
                    loan,
                    frequency,
                    occurrenceDate,
                    balance,
                    Math.Abs(input.Amount),
                    paymentNumber + 1,
                    flatInterestPaid,
                    accrualDate,
                    input.TransactionId,
                    interestDueOverride: remainingInterestForPeriod);

                payments.Add(split);
                balance = split.BalanceAfter;
                flatInterestPaid = LoanAmortization.RoundMoney(flatInterestPaid + split.Interest);
                totalInterestPaid = LoanAmortization.RoundMoney(totalInterestPaid + split.Interest);
                totalPaidForPeriod = LoanAmortization.RoundMoney(totalPaidForPeriod + split.Payment);
                remainingInterestForPeriod = LoanAmortization.RoundMoney(Math.Max(0m, remainingInterestForPeriod - split.Interest));

                if (split.BalanceBefore > 0m && split.BalanceAfter <= 0m)
                {
                    payoffDate ??= occurrenceDate;
                }
            }

            if (totalPaidForPeriod >= scheduledPayment || balance <= 0m)
            {
                paymentNumber++;
                accrualDate = occurrenceDate;
                lastOccurrenceComplete = true;
                lastOccurrenceRemainder = 0m;
                lastOccurrenceRemainingInterest = 0m;
            }
            else
            {
                lastOccurrenceComplete = false;
                lastOccurrenceRemainder = LoanAmortization.RoundMoney(scheduledPayment - totalPaidForPeriod);
                lastOccurrenceRemainingInterest = remainingInterestForPeriod;
            }
        }

        var outstandingBalance = balance;
        var futureSchedule = new List<LoanScheduleEntry>();
        DateOnly nextDate;

        if (!lastOccurrenceDate.HasValue)
        {
            nextDate = FindOccurrenceOnOrAfter(scheduleStartDate!.Value, dueDay!.Value, frequency!, loan.TrackingStartDate);
        }
        else if (!lastOccurrenceComplete)
        {
            nextDate = lastOccurrenceDate.Value;
        }
        else
        {
            nextDate = AddPeriod(lastOccurrenceDate.Value, frequency, dueDay);
        }

        var futurePeriodLimit = loan.InterestMethod == LoanInterestMethod.InterestOnly
            ? Math.Max(0, loan.TermPeriods - paymentNumber)
            : 600;

        for (var i = 0; i < futurePeriodLimit && balance > 0m; i++)
        {
            LoanScheduleEntry entry;
            if (!lastOccurrenceComplete && i == 0)
            {
                var futureNumber = paymentNumber + 1;
                var paymentAmt = Math.Min(lastOccurrenceRemainder, balance + lastOccurrenceRemainingInterest);
                var interest = Math.Min(paymentAmt, lastOccurrenceRemainingInterest);
                var principal = Math.Min(balance, Math.Max(0m, paymentAmt - interest));
                var balanceAfter = LoanAmortization.RoundMoney(Math.Max(0m, balance - principal));
                entry = new LoanScheduleEntry(nextDate, paymentAmt, interest, principal, balanceAfter);

                futureSchedule.Add(entry);
                balance = entry.BalanceAfter;
                flatInterestPaid = LoanAmortization.RoundMoney(flatInterestPaid + entry.Interest);
                accrualDate = nextDate;
                paymentNumber++;
                if (balance <= 0m)
                {
                    payoffDate ??= nextDate;
                    break;
                }
                nextDate = AddPeriod(nextDate, frequency, dueDay);
                continue;
            }

            var futureNum = paymentNumber + 1;
            entry = LoanAmortization.ApplyScheduledPayment(
                loan,
                frequency,
                nextDate,
                balance,
                futureNum,
                flatInterestPaid,
                accrualDate);

            futureSchedule.Add(entry);
            balance = entry.BalanceAfter;
            flatInterestPaid = LoanAmortization.RoundMoney(flatInterestPaid + entry.Interest);
            accrualDate = nextDate;
            paymentNumber++;
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

        // Mirrors RecurringOccurrenceService.FindOccurrenceOnOrAfter and loanMath.ts: search from
        // the later of the two bounds rather than from a month number picked out of both, which
        // went wrong whenever tracking started in a year before the schedule did.
        var floor = date > start ? date : start;
        var candidateYear = floor.Year;
        var candidateMonth = floor.Month;
        var monthlyCandidate = AnchoredDate(candidateYear, candidateMonth, dueDay);
        if (monthlyCandidate < floor)
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
