using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class RecurringOccurrenceServiceTests
{
    private readonly RecurringOccurrenceService _service =
        new(NullLogger<RecurringOccurrenceService>.Instance);

    [Fact]
    public void Monthly_CalendarCycle_ClampsDueDayToLeapYearFebruary()
    {
        var payment = Payment("Monthly", "2024-01-31", dueDate: 31);
        var range = CategoryAttributionService.GetCycleRange(2024, 2, 1);

        var occurrence = Assert.Single(_service.GetOccurrencesInRange(payment, range.start, range.end, 1));

        Assert.Equal(new DateTime(2024, 2, 29), occurrence);
    }

    [Fact]
    public void Monthly_DayThirtyOne_RecoversAfterShortMonth()
    {
        var payment = Payment("Monthly", "2026-01-31", dueDate: 31);

        var next = _service.GetNextOccurrenceOnOrAfter(payment, new DateOnly(2026, 3, 1));

        Assert.Equal(new DateOnly(2026, 3, 31), next);
    }

    [Fact]
    public void Monthly_MidMonthCycle_ReturnsOneOccurrenceInCycle()
    {
        var payment = Payment("Monthly", "2026-01-31", dueDate: 31);
        var range = CategoryAttributionService.GetCycleRange(2026, 2, 15);

        var occurrence = Assert.Single(_service.GetOccurrencesInRange(payment, range.start, range.end, 15));

        Assert.Equal(new DateTime(2026, 2, 28), occurrence);
    }

    [Fact]
    public void Annually_CalendarCycle_OnlyOccursInStartDateMonth()
    {
        var payment = Payment("Annually", "2024-02-29", dueDate: 29);
        var february = CategoryAttributionService.GetCycleRange(2025, 2, 1);
        var march = CategoryAttributionService.GetCycleRange(2025, 3, 1);

        var occurrence = Assert.Single(_service.GetOccurrencesInRange(payment, february.start, february.end, 1));

        Assert.Equal(new DateTime(2025, 2, 28), occurrence);
        Assert.Empty(_service.GetOccurrencesInRange(payment, march.start, march.end, 1));
    }

    [Fact]
    public void Annually_MidMonthCycle_UsesTheCalendarMonthContainingTheBillingDate()
    {
        var payment = Payment("Annually", "2024-02-05", dueDate: 5);
        var januaryCycle = CategoryAttributionService.GetCycleRange(2026, 1, 15);
        var februaryCycle = CategoryAttributionService.GetCycleRange(2026, 2, 15);

        var occurrence = Assert.Single(_service.GetOccurrencesInRange(
            payment,
            januaryCycle.start,
            januaryCycle.end,
            15));

        Assert.Equal(new DateTime(2026, 2, 5), occurrence);
        Assert.Empty(_service.GetOccurrencesInRange(payment, februaryCycle.start, februaryCycle.end, 15));
    }

    [Fact]
    public void OccurrenceOutsidePaymentDateRange_IsExcluded()
    {
        var payment = Payment("Monthly", "2026-02-16", dueDate: 15);
        var range = CategoryAttributionService.GetCycleRange(2026, 2, 1);

        Assert.Empty(_service.GetOccurrencesInRange(payment, range.start, range.end, 1));
    }

    [Fact]
    public void LegacyWeeklyFrequency_IsDefensivelyTreatedAsMonthly()
    {
        var payment = Payment("Weekly", "2026-01-15", dueDate: 15);
        var range = CategoryAttributionService.GetCycleRange(2026, 7, 1);

        var occurrence = Assert.Single(_service.GetOccurrencesInRange(payment, range.start, range.end, 1));

        Assert.Equal(new DateTime(2026, 7, 15), occurrence);
    }

    // A schedule whose start year is later than the year being searched from. The month was chosen
    // with Math.Max(start.Month, date.Month) whenever the resolved year equalled the start year --
    // which is also true when the search date sits in an *earlier* year. A January 2027 bill
    // searched from September 2026 therefore answered September 2027, eight months late.
    [Fact]
    public void Monthly_StartYearAfterSearchYear_ReturnsTheFirstScheduledOccurrence()
    {
        var payment = Payment("Monthly", "2027-01-10", dueDate: 10);

        var next = _service.GetNextOccurrenceOnOrAfter(payment, new DateOnly(2026, 9, 6));

        Assert.Equal(new DateOnly(2027, 1, 10), next);
    }

    [Fact]
    public void Monthly_StartYearAfterSearchYear_ClampsDueDayInTheStartMonth()
    {
        var payment = Payment("Monthly", "2027-02-28", dueDate: 31);

        var next = _service.GetNextOccurrenceOnOrAfter(payment, new DateOnly(2026, 12, 31));

        Assert.Equal(new DateOnly(2027, 2, 28), next);
    }

    // The same defect seen through the range walk, which is what tags a settlement transaction to
    // its occurrence. With the default cycle day of 28 the cycle holding a 10 January occurrence
    // opens in December, so the range started in the year before the bill did and came back empty
    // -- refusing to settle a row the occurrence ledger had already materialised.
    [Fact]
    public void Monthly_FirstOccurrenceInACycleThatOpensThePreviousYear_IsStillFound()
    {
        var payment = Payment("Monthly", "2027-01-10", dueDate: 10);
        var range = CategoryAttributionService.GetCycleRange(2026, 12, 28);

        var occurrence = Assert.Single(_service.GetOccurrencesInRange(payment, range.start, range.end, 28));

        Assert.Equal(new DateTime(2027, 1, 10), occurrence);
    }

    [Fact]
    public void Annually_StartYearAfterSearchYear_ReturnsTheFirstScheduledOccurrence()
    {
        var payment = Payment("Annually", "2027-03-15", dueDate: 15);

        var next = _service.GetNextOccurrenceOnOrAfter(payment, new DateOnly(2026, 11, 1));

        Assert.Equal(new DateOnly(2027, 3, 15), next);
    }

    private static RecurringPayment Payment(string frequency, string startDate, int dueDate) => new()
    {
        Id = "payment-1",
        Name = "Payment",
        Frequency = frequency,
        StartDate = startDate,
        DueDate = dueDate,
        Active = true
    };
}
