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

        var dueDay = Math.Clamp(payment.DueDate, 1, 31);
        var billingDate = CategoryAttributionService.GetBillingDateForCycle(
            cycleStart,
            cycleEnd,
            cycleDay,
            dueDay);

        if (string.Equals(frequency, "Annually", StringComparison.OrdinalIgnoreCase) &&
            billingDate.Month != startDate.Month)
        {
            return [];
        }

        if (billingDate < cycleStart ||
            billingDate > cycleEnd ||
            billingDate < startDate ||
            (endDate.HasValue && billingDate > endDate.Value))
        {
            return [];
        }

        return [billingDate];
    }

    /// <summary>
    /// Returns whether a transaction settles a particular occurrence. Callers must first scope
    /// their transaction set to the relevant cycle/occurrence range; a null occurrence date is the
    /// documented legacy fallback and is therefore accepted for the scoped posting date.
    /// </summary>
    public static bool MatchesOccurrence(
        Transaction transaction,
        string recurringPaymentId,
        DateOnly occurrenceDate)
    {
        return string.Equals(transaction.RecurringPaymentId, recurringPaymentId, StringComparison.Ordinal) &&
            (transaction.RecurringOccurrenceDate == null || transaction.RecurringOccurrenceDate == occurrenceDate);
    }

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
