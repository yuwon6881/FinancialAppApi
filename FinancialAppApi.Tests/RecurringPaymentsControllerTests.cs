using FinancialAppApi.Controllers;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FinancialAppApi.Tests;

public class RecurringPaymentsControllerTests
{
    [Fact]
    public async Task GetRecurringPayments_RoundTripsReminderConfiguration()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var payment = NewPayment("rec-1");
        payment.PushReminderEnabled = true;
        payment.PushReminderMode = "Daily";
        payment.PushReminderLeadDays = 7;
        context.RecurringPayments.Add(payment);
        await context.SaveChangesAsync();
        var controller = NewController(context);

        var result = await controller.GetRecurringPayments();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.Single(Assert.IsType<List<RecurringPaymentDto>>(ok.Value));
        Assert.True(dto.ReminderEnabled);
        Assert.Equal("Daily", dto.ReminderMode);
        Assert.Equal(7, dto.ReminderLeadDays);
    }

    [Theory]
    [InlineData("Weekly")]
    [InlineData("Daily")]
    [InlineData("")]
    public async Task PostRecurringPayment_RejectsUnsupportedFrequency(string frequency)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = NewController(context);

        var result = await controller.PostRecurringPayment(new RecurringPaymentDto { Frequency = frequency });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(context.RecurringPayments);
    }

    [Fact]
    public async Task PutRecurringPayment_RejectsUnsupportedFrequency()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = NewController(context);

        var result = await controller.PutRecurringPayment(
            "payment-1",
            new RecurringPaymentDto { Id = "payment-1", Frequency = "Weekly" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PutReminder_ReturnsUpdatedReminderSettings()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1"));
        await context.SaveChangesAsync();
        var controller = NewController(context);

        var result = await controller.PutReminder(
            "rec-1",
            new RecurringPaymentReminderDto { Enabled = true, Mode = "Daily", LeadDays = 3 });

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<RecurringPaymentReminderDto>(ok.Value);
        Assert.True(dto.Enabled);
        Assert.Equal("Daily", dto.Mode);
        Assert.Equal(3, dto.LeadDays);
    }

    [Fact]
    public async Task PutReminder_ReturnsNotFound_ForMissingPayment()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = NewController(context);

        var result = await controller.PutReminder(
            "missing",
            new RecurringPaymentReminderDto { Enabled = true, Mode = "Once", LeadDays = 1 });

        Assert.IsType<NotFoundResult>(result);
    }

    [Theory]
    [InlineData("Countdown")]
    [InlineData("")]
    public async Task PutReminder_RejectsUnsupportedMode(string mode)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1"));
        await context.SaveChangesAsync();
        var controller = NewController(context);

        var result = await controller.PutReminder(
            "rec-1",
            new RecurringPaymentReminderDto { Enabled = true, Mode = mode, LeadDays = 1 });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task PutReminder_RejectsUnsupportedLeadDays(int leadDays)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1"));
        await context.SaveChangesAsync();
        var controller = NewController(context);

        var result = await controller.PutReminder(
            "rec-1",
            new RecurringPaymentReminderDto { Enabled = true, Mode = "Once", LeadDays = leadDays });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PostPayEarly_ReturnsTransactionAndSettledOccurrenceDate_OnSuccess()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1"));
        await context.SaveChangesAsync();
        var controller = NewController(context);

        var result = await controller.PostPayEarly("rec-1", NextOccurrenceRequest());

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task PostPayEarly_ReturnsNotFound_ForMissingPayment()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = NewController(context);

        var result = await controller.PostPayEarly("missing", NextOccurrenceRequest());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task PostPayEarly_ReturnsBadRequest_ForInactivePayment()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", active: false));
        await context.SaveChangesAsync();
        var controller = NewController(context);

        var result = await controller.PostPayEarly("rec-1", NextOccurrenceRequest());

        Assert.IsType<BadRequestObjectResult>(result);
    }

    private static RecurringPayment NewPayment(
        string id,
        bool active = true,
        string paymentMode = RecurringPaymentMode.Manual)
    {
        return new RecurringPayment
        {
            PaymentMode = paymentMode,
            Id = id,
            Name = "Payment",
            Amount = 100m,
            Frequency = "Monthly",
            Category = "Bills",
            LedgerCategory = "Essentials",
            NextDueDate = "2026-01-15",
            DueDate = 15,
            StartDate = "2026-01-01",
            Active = active
        };
    }

    [Fact]
    public async Task PostPayEarly_ReturnsBadRequest_ForAutoDeductedPayment()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", paymentMode: RecurringPaymentMode.AutoDeduct));
        await context.SaveChangesAsync();
        var controller = NewController(context);

        var result = await controller.PostPayEarly("rec-1", NextOccurrenceRequest());

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(context.Transactions);
    }

    [Fact]
    public async Task GetRecurringPayments_RoundTripsPaymentMode()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", paymentMode: RecurringPaymentMode.AutoDeduct));
        await context.SaveChangesAsync();
        var controller = NewController(context);

        var result = await controller.GetRecurringPayments();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.Single(Assert.IsType<List<RecurringPaymentDto>>(ok.Value));
        Assert.Equal(RecurringPaymentMode.AutoDeduct, dto.PaymentMode);
    }

    [Theory]
    [InlineData("Automatic")]
    [InlineData("")]
    public async Task PostRecurringPayment_RejectsUnsupportedPaymentMode(string paymentMode)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = NewController(context);

        var result = await controller.PostRecurringPayment(new RecurringPaymentDto
        {
            Frequency = "Monthly",
            Amount = Database.ObfuscationHelper.Obfuscate(100m),
            PaymentMode = paymentMode
        });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(context.RecurringPayments);
    }

    [Fact]
    public async Task PostRecurringPayment_PersistsPaymentMode()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.TransactionCategories.Add(new TransactionCategory { Id = "cat-bills", Name = "Bills" });
        await context.SaveChangesAsync();
        var controller = NewController(context);

        var result = await controller.PostRecurringPayment(new RecurringPaymentDto
        {
            Id = "rec-1",
            Name = "Insurance",
            Amount = Database.ObfuscationHelper.Obfuscate(100m),
            Frequency = "Monthly",
            Category = "Bills",
            LedgerCategory = "Essentials",
            NextDueDate = "2026-01-15",
            DueDate = 15,
            StartDate = "2026-01-01",
            Active = true,
            PaymentMode = RecurringPaymentMode.AutoDeduct
        });

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var stored = Assert.Single(context.RecurringPayments);
        Assert.Equal(RecurringPaymentMode.AutoDeduct, stored.PaymentMode);
    }

    [Fact]
    public async Task PutRecurringPayment_RejectsUnsupportedPaymentMode()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var controller = NewController(context);

        var result = await controller.PutRecurringPayment(
            "payment-1",
            new RecurringPaymentDto
            {
                Id = "payment-1",
                Frequency = "Monthly",
                Amount = Database.ObfuscationHelper.Obfuscate(100m),
                PaymentMode = "Direct debit"
            });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    private static PayEarlyRequestDto NextOccurrenceRequest()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var year = today.Year;
        var month = today.Month;
        if (today.Day >= 15)
        {
            month++;
            if (month == 13)
            {
                month = 1;
                year++;
            }
        }
        return new PayEarlyRequestDto { OccurrenceDate = new DateOnly(year, month, 15).ToString("yyyy-MM-dd") };
    }

    private static RecurringPaymentsController NewController(Database.AppDbContext context)
    {
        var occurrenceService = new RecurringOccurrenceService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RecurringOccurrenceService>.Instance);
        return new RecurringPaymentsController(
            new RecurringPaymentService(context),
            new RecurringPaymentPayEarlyService(
                context,
                occurrenceService,
                new CycleBalanceService(context)))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }
}
