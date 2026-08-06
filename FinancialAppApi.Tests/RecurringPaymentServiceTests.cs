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
    public async Task GetRecurringPaymentsAsync_OrdersByNameThenStableId()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.AddRange(
            NewPayment("rec-b", "Streaming"),
            NewPayment("rec-z", "Utilities"),
            NewPayment("rec-a", "Streaming"));
        await context.SaveChangesAsync();

        var payments = await new RecurringPaymentService(context).GetRecurringPaymentsAsync();

        Assert.Equal(["rec-a", "rec-b", "rec-z"], payments.Select(payment => payment.Id));
    }

    [Fact]
    public async Task CreateRecurringPaymentAsync_PersistsPayment()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        await context.SaveChangesAsync();
        var service = new RecurringPaymentService(context);

        var result = await service.CreateRecurringPaymentAsync(NewPayment("rec-1", "Internet"));

        Assert.Equal(CreateRecurringPaymentStatus.Created, result.Status);
        Assert.True(await context.RecurringPayments.AnyAsync(p => p.Id == "rec-1"));
    }

    [Fact]
    public async Task CreateRecurringPaymentAsync_DedupesReplayOnClientId()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        await context.SaveChangesAsync();
        var service = new RecurringPaymentService(context);

        var first = await service.CreateRecurringPaymentAsync(NewPayment("rec-1", "Internet"));
        // Replay of the same offline create (e.g. lost-response retry): same client id must
        // return the existing row as success, not throw a duplicate-PK 500.
        var second = await service.CreateRecurringPaymentAsync(NewPayment("rec-1", "Internet"));

        Assert.Equal(CreateRecurringPaymentStatus.Created, first.Status);
        Assert.Equal(CreateRecurringPaymentStatus.Existing, second.Status);
        Assert.Equal(1, await context.RecurringPayments.CountAsync());
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
    public async Task ToggleActiveAsync_SetsAbsoluteStateIdempotently()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", "Internet", active: true));
        await context.SaveChangesAsync();
        var service = new RecurringPaymentService(context);

        // The offline outbox coalesces multiple toggles into one op carrying the final desired
        // state and may replay it on retry. Sending the absolute state must be idempotent: two
        // calls with active:false leave it false (a relative flip would have flipped it back on).
        var first = await service.ToggleActiveAsync("rec-1", active: false);
        var second = await service.ToggleActiveAsync("rec-1", active: false);

        Assert.NotNull(first);
        Assert.False(first.Active);
        Assert.NotNull(second);
        Assert.False(second.Active);
    }

    [Fact]
    public async Task ToggleActiveAsync_FallsBackToRelativeFlipWithoutState()
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
        SeedCategories(context);
        context.RecurringPayments.Add(NewPayment("rec-1", "Internet"));
        await context.SaveChangesAsync();
        var service = new RecurringPaymentService(context);

        var result = await service.UpdateRecurringPaymentAsync("rec-1", NewPayment("rec-1", "Fiber", amount: 120m));

        Assert.Equal(UpdateRecurringPaymentStatus.Updated, result.Status);
        Assert.Equal("Fiber", result.Payment!.Name);
        Assert.Equal(120m, result.Payment.Amount);
    }

    [Fact]
    public async Task UpdateRecurringPaymentAsync_ChangesPaymentMode()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        SeedCategories(context);
        context.RecurringPayments.Add(NewPayment("rec-1", "Internet", paymentMode: RecurringPaymentMode.Manual));
        await context.SaveChangesAsync();
        var service = new RecurringPaymentService(context);

        var result = await service.UpdateRecurringPaymentAsync(
            "rec-1",
            NewPayment("rec-1", "Internet", paymentMode: RecurringPaymentMode.AutoDeduct));

        Assert.Equal(UpdateRecurringPaymentStatus.Updated, result.Status);
        Assert.Equal(RecurringPaymentMode.AutoDeduct, result.Payment!.PaymentMode);
    }

    [Fact]
    public async Task GetRecurringPaymentsAsync_ProjectsPaymentMode()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", "Internet", paymentMode: RecurringPaymentMode.AutoDeduct));
        await context.SaveChangesAsync();

        var payments = await new RecurringPaymentService(context).GetRecurringPaymentsAsync();

        Assert.Equal(RecurringPaymentMode.AutoDeduct, payments[0].PaymentMode);
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

    [Fact]
    public async Task UpdateReminderAsync_UpdatesEnabledModeAndLeadDays()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", "Internet"));
        await context.SaveChangesAsync();
        var service = new RecurringPaymentService(context);

        var result = await service.UpdateReminderAsync("rec-1", enabled: true, mode: "daily", leadDays: 3);

        Assert.Equal(UpdateReminderStatus.Updated, result.Status);
        Assert.True(result.Payment!.PushReminderEnabled);
        Assert.Equal("Daily", result.Payment.PushReminderMode);
        Assert.Equal(3, result.Payment.PushReminderLeadDays);
    }

    [Fact]
    public async Task UpdateReminderAsync_ReturnsNotFoundForMissingPayment()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = new RecurringPaymentService(context);

        var result = await service.UpdateReminderAsync("missing", true, "Once", 1);

        Assert.Equal(UpdateReminderStatus.NotFound, result.Status);
    }

    [Theory]
    [InlineData("Weekly")]
    [InlineData("Countdown")]
    [InlineData("")]
    public async Task UpdateReminderAsync_RejectsUnsupportedMode(string mode)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", "Internet"));
        await context.SaveChangesAsync();
        var service = new RecurringPaymentService(context);

        var result = await service.UpdateReminderAsync("rec-1", true, mode, 1);

        Assert.Equal(UpdateReminderStatus.InvalidMode, result.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(-1)]
    public async Task UpdateReminderAsync_RejectsUnsupportedLeadDays(int leadDays)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", "Internet"));
        await context.SaveChangesAsync();
        var service = new RecurringPaymentService(context);

        var result = await service.UpdateReminderAsync("rec-1", true, "Once", leadDays);

        Assert.Equal(UpdateReminderStatus.InvalidLeadDays, result.Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task UpdateReminderAsync_AcceptsAllowedLeadDays(int leadDays)
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.RecurringPayments.Add(NewPayment("rec-1", "Internet"));
        await context.SaveChangesAsync();
        var service = new RecurringPaymentService(context);

        var result = await service.UpdateReminderAsync("rec-1", true, "Once", leadDays);

        Assert.Equal(UpdateReminderStatus.Updated, result.Status);
        Assert.Equal(leadDays, result.Payment!.PushReminderLeadDays);
    }

    private static RecurringPayment NewPayment(
        string id,
        string name,
        decimal amount = 100m,
        bool active = true,
        string paymentMode = RecurringPaymentMode.Manual)
    {
        return new RecurringPayment
        {
            PaymentMode = paymentMode,
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

    private static void SeedCategories(Database.AppDbContext context)
    {
        context.TransactionCategories.Add(new TransactionCategory { Id = "cat-bills", Name = "Bills" });
    }
}
