using System.Collections.Concurrent;
using System.Globalization;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services;

public class RecurringOccurrenceService
{
    private readonly ILogger<RecurringOccurrenceService> _logger;
    private readonly ConcurrentDictionary<string, byte> _loggedLegacyWeeklyPayments = new();

    public RecurringOccurrenceService(ILogger<RecurringOccurrenceService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<DateTime> GetOccurrencesInRange(
        RecurringPayment payment,
        DateTime cycleStart,
        DateTime cycleEnd,
        int cycleDay)
    {
        if (!TryParseDate(payment.StartDate, out var startDate))
        {
            return [];
        }

        DateTime? endDate = null;
        if (!string.IsNullOrWhiteSpace(payment.EndDate))
        {
            if (!TryParseDate(payment.EndDate, out var parsedEndDate))
            {
                return [];
            }
            endDate = parsedEndDate;
        }

        var frequency = payment.Frequency;
        if (string.Equals(frequency, "Weekly", StringComparison.OrdinalIgnoreCase))
        {
            frequency = "Monthly";
            if (_loggedLegacyWeeklyPayments.TryAdd(payment.Id, 0))
            {
                _logger.LogWarning(
                    "Recurring payment {RecurringPaymentId} has legacy Weekly frequency; treating it as Monthly.",
                    payment.Id);
            }
        }

        if (!string.Equals(frequency, "Monthly", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(frequency, "Annually", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Recurring payment {RecurringPaymentId} has unsupported frequency {Frequency}; no occurrence was generated.",
                payment.Id,
                payment.Frequency);
            return [];
        }

        var cursor = DateOnly.FromDateTime(cycleStart);
        var rangeEnd = DateOnly.FromDateTime(cycleEnd);
        var result = new List<DateTime>();
        var paymentStart = DateOnly.FromDateTime(startDate);
        var paymentEnd = endDate.HasValue ? DateOnly.FromDateTime(endDate.Value) : (DateOnly?)null;
        var annual = string.Equals(frequency, "Annually", StringComparison.OrdinalIgnoreCase);

        var next = FindOccurrenceOnOrAfter(paymentStart, Math.Clamp(payment.DueDate, 1, 31), annual, cursor);
        while (next <= rangeEnd && (!paymentEnd.HasValue || next <= paymentEnd.Value))
        {
            if (next >= paymentStart) result.Add(next.ToDateTime(TimeOnly.MinValue));
            next = AddAnchoredPeriod(next, paymentStart.Month, Math.Clamp(payment.DueDate, 1, 31), annual);
        }

        return result;
    }

    public DateOnly? GetNextOccurrenceOnOrAfter(RecurringPayment payment, DateOnly date)
    {
        if (!TryParseDate(payment.StartDate, out var parsedStart)) return null;
        var start = DateOnly.FromDateTime(parsedStart);
        var annual = string.Equals(payment.Frequency, "Annually", StringComparison.OrdinalIgnoreCase);
        if (!annual && !string.Equals(payment.Frequency, "Monthly", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(payment.Frequency, "Weekly", StringComparison.OrdinalIgnoreCase)) return null;

        var next = FindOccurrenceOnOrAfter(start, Math.Clamp(payment.DueDate, 1, 31), annual, date);
        if (!string.IsNullOrWhiteSpace(payment.EndDate) &&
            DateOnly.TryParseExact(payment.EndDate, "yyyy-MM-dd", out var end) && next > end) return null;
        return next;
    }

    private static DateOnly FindOccurrenceOnOrAfter(DateOnly start, int dueDay, bool annual, DateOnly date)
    {
        if (annual)
        {
            var annualYear = Math.Max(start.Year, date.Year);
            var annualCandidate = AnchoredDate(annualYear, start.Month, dueDay);
            if (annualCandidate < start || annualCandidate < date) annualCandidate = AnchoredDate(annualYear + 1, start.Month, dueDay);
            return annualCandidate;
        }

        // Search from whichever of the two bounds is later, then take that month's anchored day.
        // Deriving the month from Math.Max(start.Month, date.Month) looked equivalent but was not:
        // the guard it sat behind is also true when `date` falls in a year *before* the schedule
        // starts, and it then skipped to the later of two unrelated month numbers.
        var floor = date > start ? date : start;
        var year = floor.Year;
        var month = floor.Month;
        var candidate = AnchoredDate(year, month, dueDay);
        // At most one step: the next month begins after `floor`, so its anchored day cannot precede it.
        if (candidate < floor)
        {
            month++;
            if (month == 13) { month = 1; year++; }
            candidate = AnchoredDate(year, month, dueDay);
        }
        return candidate;
    }

    private static DateOnly AddAnchoredPeriod(DateOnly current, int annualMonth, int dueDay, bool annual)
    {
        if (annual) return AnchoredDate(current.Year + 1, annualMonth, dueDay);
        var year = current.Year;
        var month = current.Month + 1;
        if (month == 13) { month = 1; year++; }
        return AnchoredDate(year, month, dueDay);
    }

    private static DateOnly AnchoredDate(int year, int month, int dueDay) =>
        new(year, month, Math.Min(dueDay, DateTime.DaysInMonth(year, month)));

    private static bool TryParseDate(string? value, out DateTime date)
    {
        if (DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            date = parsed.ToDateTime(TimeOnly.MinValue);
            return true;
        }

        date = default;
        return false;
    }
}
