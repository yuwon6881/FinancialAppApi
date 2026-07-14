using FinancialAppApi.Controllers;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FinancialAppApi.Tests;

public class RecurringPaymentsControllerTests
{
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

    private static RecurringPaymentsController NewController(Database.AppDbContext context)
    {
        return new RecurringPaymentsController(new RecurringPaymentService(context))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }
}
