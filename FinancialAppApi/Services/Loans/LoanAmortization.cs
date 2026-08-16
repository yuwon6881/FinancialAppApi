using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Loans;

public sealed record LoanPaymentInput(
    DateOnly OccurrenceDate,
    DateTime PostedAt,
    decimal Amount,
    bool IsDiscarded = false,
    string? TransactionId = null);

public sealed record LoanPaymentSplit(
    DateOnly OccurrenceDate,
    decimal Payment,
    decimal Interest,
    decimal Principal,
    decimal BalanceBefore,
    decimal BalanceAfter,
    decimal Surplus,
    bool PaymentDidNotCoverInterest,
    string? TransactionId = null);

public sealed record LoanScheduleEntry(
    DateOnly OccurrenceDate,
    decimal Payment,
    decimal Interest,
    decimal Principal,
    decimal BalanceAfter);

/// <summary>
/// Pure loan arithmetic. Money is rounded with the same away-from-zero convention as the ledger;
/// interest is rounded first and the principal is then derived from the actual payment so the
/// displayed parts always add back to that payment.
/// </summary>
public static class LoanAmortization
{
    // A fixed 365-day denominator keeps the same loan cost identical on either side of 29 February
    // and lets the frontend reproduce the calculation without a second leap-year rule.
    private const decimal DaysPerYear = 365m;

    public static int PeriodsPerYear(string? frequency) =>
        string.Equals(frequency, "Annually", StringComparison.OrdinalIgnoreCase) ? 1 : 12;

    public static decimal RoundMoney(decimal value) =>
        MoneyRounding.RoundMoney(value);

    public static decimal AnnualRate(decimal annualRatePercent) => annualRatePercent / 100m;

    public static decimal TotalScheduledInterest(Loan loan, string? frequency)
    {
        var periodsPerYear = PeriodsPerYear(frequency);
        var rate = AnnualRate(loan.AnnualRatePercent);
        if (loan.InterestMethod is LoanInterestMethod.Flat or LoanInterestMethod.InterestOnly)
        {
            return RoundMoney(loan.OpeningPrincipal * rate * loan.TermPeriods / periodsPerYear);
        }

        var payment = ScheduledPayment(loan, frequency);
        return RoundMoney(Math.Max(0m, payment * loan.TermPeriods - loan.OpeningPrincipal));
    }

    public static decimal ScheduledPayment(Loan loan, string? frequency)
    {
        var periods = Math.Max(1, loan.TermPeriods);
        var periodsPerYear = PeriodsPerYear(frequency);
        var ratePerPeriod = AnnualRate(loan.AnnualRatePercent) / periodsPerYear;
        if (loan.InterestMethod == LoanInterestMethod.Flat)
        {
            var totalInterest = loan.OpeningPrincipal * AnnualRate(loan.AnnualRatePercent)
                * periods / periodsPerYear;
            return RoundMoney((loan.OpeningPrincipal + totalInterest) / periods);
        }
        if (loan.InterestMethod == LoanInterestMethod.InterestOnly)
        {
            return RoundMoney(loan.OpeningPrincipal * ratePerPeriod);
        }

        if (ratePerPeriod == 0m) return RoundMoney(loan.OpeningPrincipal / periods);

        // Computing the positive power overflows decimal for valid annual loans such as 25%
        // over 300 periods. The formula only needs its reciprocal, which can be accumulated
        // without ever leaving the decimal range and naturally underflows to zero at extreme
        // rates instead of throwing.
        var inversePower = InverseDecimalPower(1m + ratePerPeriod, periods);
        var denominator = 1m - inversePower;
        return RoundMoney(loan.OpeningPrincipal * ratePerPeriod / denominator);
    }

    public static LoanPaymentSplit ApplyPayment(
        Loan loan,
        string? frequency,
        DateOnly occurrenceDate,
        decimal balanceBefore,
        decimal payment,
        int paymentNumber,
        decimal flatInterestPaidBefore,
        DateOnly previousAccrualDate,
        string? transactionId = null)
    {
        var actualPayment = RoundMoney(Math.Max(0m, payment));
        var normalizedBalance = RoundMoney(Math.Max(0m, balanceBefore));
        if (normalizedBalance <= 0m)
        {
            return new LoanPaymentSplit(
                occurrenceDate, actualPayment, 0m, 0m, 0m, 0m, actualPayment, false, transactionId);
        }

        var interestDue = loan.InterestMethod switch
        {
            LoanInterestMethod.Flat => FlatInterestForPayment(loan, frequency, paymentNumber, flatInterestPaidBefore),
            LoanInterestMethod.ReducingBalanceDaily => DailyInterest(
                loan,
                normalizedBalance,
                previousAccrualDate,
                occurrenceDate),
            _ => RoundMoney(normalizedBalance * AnnualRate(loan.AnnualRatePercent) / PeriodsPerYear(frequency))
        };
        var interest = Math.Min(actualPayment, Math.Max(0m, interestDue));
        var didNotCoverInterest = actualPayment < interestDue && interestDue > 0m;
        var principal = didNotCoverInterest
            ? 0m
            : Math.Min(normalizedBalance, RoundMoney(actualPayment - interest));
        var balanceAfter = RoundMoney(Math.Max(0m, normalizedBalance - principal));
        var surplus = RoundMoney(Math.Max(0m, actualPayment - interest - principal));

        return new LoanPaymentSplit(
            occurrenceDate,
            actualPayment,
            RoundMoney(interest),
            RoundMoney(principal),
            normalizedBalance,
            balanceAfter,
            surplus,
            didNotCoverInterest,
            transactionId);
    }

    public static LoanScheduleEntry ApplyScheduledPayment(
        Loan loan,
        string? frequency,
        DateOnly occurrenceDate,
        decimal balanceBefore,
        int paymentNumber,
        decimal flatInterestPaidBefore,
        DateOnly previousAccrualDate)
    {
        var split = ApplyPayment(
            loan,
            frequency,
            occurrenceDate,
            balanceBefore,
            ScheduledPayment(loan, frequency),
            paymentNumber,
            flatInterestPaidBefore,
            previousAccrualDate);
        return new LoanScheduleEntry(
            occurrenceDate,
            split.Payment,
            split.Interest,
            split.Principal,
            split.BalanceAfter);
    }

    public static decimal TotalScheduledInterestForFrequency(Loan loan, string? frequency) =>
        TotalScheduledInterest(loan, frequency);

    private static decimal FlatInterestForPayment(
        Loan loan,
        string? frequency,
        int paymentNumber,
        decimal flatInterestPaidBefore)
    {
        var totalInterest = TotalScheduledInterestForFrequency(loan, frequency);
        var remainingInterest = Math.Max(0m, totalInterest - flatInterestPaidBefore);
        return paymentNumber >= Math.Max(1, loan.TermPeriods)
            ? remainingInterest
            : Math.Min(remainingInterest, RoundMoney(totalInterest / Math.Max(1, loan.TermPeriods)));
    }

    private static decimal DailyInterest(
        Loan loan,
        decimal balance,
        DateOnly previousAccrualDate,
        DateOnly occurrenceDate)
    {
        var days = occurrenceDate.DayNumber - previousAccrualDate.DayNumber;
        if (days <= 0) return 0m;
        return RoundMoney(balance * AnnualRate(loan.AnnualRatePercent) * days / DaysPerYear);
    }

    private static decimal InverseDecimalPower(decimal value, int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++) result /= value;
        return result;
    }
}
