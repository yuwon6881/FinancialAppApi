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
