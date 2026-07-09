using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Tests;

public class RecurringPaymentServiceTests
{
    [Fact]
    public async Task GetRecurringPaymentsAsync_ReturnsPayments()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", "Rent"));
        await context.SaveChangesAsync();
        var service = new RecurringPaymentService(context);

        var payments = await service.GetRecurringPaymentsAsync();

        Assert.Single(payments);
        Assert.Equal("Rent", payments[0].Name);
    }

    [Fact]
    public async Task CreateRecurringPaymentAsync_PersistsPayment()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = new RecurringPaymentService(context);

        await service.CreateRecurringPaymentAsync(NewPayment("rec-1", "Internet"));

        Assert.True(await context.RecurringPayments.AnyAsync(p => p.Id == "rec-1"));
    }

    [Fact]
    public async Task ToggleActiveAsync_TogglesExistingPayment()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", "Internet", active: true));
        await context.SaveChangesAsync();
        var service = new RecurringPaymentService(context);

        var payment = await service.ToggleActiveAsync("rec-1");

        Assert.NotNull(payment);
        Assert.False(payment.Active);
    }

    [Fact]
    public async Task UpdateRecurringPaymentAsync_UpdatesExistingPayment()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", "Internet"));
        await context.SaveChangesAsync();
        var service = new RecurringPaymentService(context);

        var result = await service.UpdateRecurringPaymentAsync("rec-1", NewPayment("rec-1", "Fiber", amount: 120m));

        Assert.Equal(UpdateRecurringPaymentStatus.Updated, result.Status);
        Assert.Equal("Fiber", result.Payment!.Name);
        Assert.Equal(120m, result.Payment.Amount);
    }

    [Fact]
    public async Task DeleteRecurringPaymentAsync_RemovesExistingPayment()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", "Internet"));
        await context.SaveChangesAsync();
        var service = new RecurringPaymentService(context);

        var deleted = await service.DeleteRecurringPaymentAsync("rec-1");

        Assert.True(deleted);
        Assert.False(await context.RecurringPayments.AnyAsync(p => p.Id == "rec-1"));
    }

    private static RecurringPayment NewPayment(
        string id,
        string name,
        decimal amount = 100m,
        bool active = true)
    {
        return new RecurringPayment
        {
            Id = id,
            Name = name,
            Amount = amount,
            Frequency = "Monthly",
            Category = "Bills",
            LedgerCategory = "Essentials",
            NextDueDate = "2026-07-15",
            DueDate = 15,
            StartDate = "2026-01-01",
            Active = active
        };
    }
}
