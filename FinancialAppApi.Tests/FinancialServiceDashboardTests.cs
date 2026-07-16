using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class FinancialServiceDashboardTests
{
    // cycleDay = 1 so cycles line up exactly with calendar months, keeping the seeded dataset
    // easy to reason about. Active cycle is Jul 2026 (today's date in this test's timeline).
    private static Transaction Tx(string id, int year, int month, int day, string category, decimal amount, string ledgerCategory = "Essentials")
    {
        return new Transaction
        {
            Id = id,
            Date = new DateTime(year, month, day),
            Description = id,
            Category = category,
            LedgerCategory = ledgerCategory,
            Amount = amount
        };
    }

    [Fact]
    public async Task GetDashboardInsightsAsync_ComputesBreakdownsAndRewardsAverageFromConsolidatedRangeQueries()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026 });
        context.Transactions.AddRange(
            Tx("jan", 2026, 1, 15, "Growth", -1000000m),
            Tx("feb", 2026, 2, 15, "Hobbies", -5m),
            Tx("mar", 2026, 3, 15, "Software", -15m),
            Tx("apr", 2026, 4, 15, "Food", -200m),
            Tx("may-food", 2026, 5, 15, "Entertainment", -20m),
            Tx("may-reward", 2026, 5, 20, "Other", 10m, "Rewards"),
            Tx("jun-food", 2026, 6, 15, "Food", -50m),
            Tx("jun-reward", 2026, 6, 20, "Other", 30m, "Rewards"),
            Tx("jul", 2026, 7, 9, "Food", -100m),
            Tx("jul-reward", 2026, 7, 10, "Other", 900m, "Rewards"),
            Tx("dec", 2026, 12, 15, "Stability", -7m));
        await context.SaveChangesAsync();
        var service = NewService(context);

        var response = await service.GetDashboardInsightsAsync("Jul", 2026);

        var yearly = GetBreakdown(response, "yearlyCategoryBreakdown");
        Assert.Equal(
            new[] { ("Growth", 1000000m), ("Food", 350m), ("Entertainment", 20m), ("Software", 15m), ("Stability", 7m), ("Hobbies", 5m) },
            yearly);

        var last6 = GetBreakdown(response, "last6CategoryBreakdown");
        Assert.Equal(
            new[] { ("Food", 350m), ("Entertainment", 20m), ("Software", 15m), ("Hobbies", 5m) },
            last6);

        var last3 = GetBreakdown(response, "last3CategoryBreakdown");
        Assert.Equal(
            new[] { ("Food", 150m), ("Entertainment", 20m) },
            last3);

        // ObfuscationHelper.Obfuscate rounds to 2dp ("0.00") when encoding, so the roundtrip
        // loses precision beyond cents -- expect the rounded value, not the exact fraction.
        var averageRaw = response.GetType().GetProperty("pastThreeMonthsRewardsAverage")!.GetValue(response) as string;
        var average = ObfuscationHelper.Deobfuscate(averageRaw!);
        Assert.Equal(13.33m, average);
        Assert.True((bool)response.GetType().GetProperty("hasRewardsHistory")!.GetValue(response)!);

        var availableYears = (System.Collections.IEnumerable)response.GetType().GetProperty("availableYears")!.GetValue(response)!;
        Assert.Equal(new[] { 2026 }, availableYears.Cast<int>().ToArray());
    }

    [Fact]
    public async Task GetDashboardInsightsAsync_DoesNotPersistSelectedPeriod()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jan", SelectedYear = 2025 });
        await context.SaveChangesAsync();
        var service = NewService(context);

        await service.GetDashboardInsightsAsync("Jul", 2026);

        var setting = context.FinancialSettings.Single();
        Assert.Equal("Jan", setting.SelectedMonth);
        Assert.Equal(2025, setting.SelectedYear);
    }

    [Fact]
    public async Task GetDashboardDataAsync_NoLongerReturnsHistoricalInsightsFields()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.Transactions.Add(Tx("jul", 2026, 7, 9, "Food", -100m));
        await context.SaveChangesAsync();
        var service = NewService(context);

        var response = await service.GetDashboardDataAsync("Jul", 2026);
        var responseType = response.GetType();

        Assert.Null(responseType.GetProperty("last3CategoryBreakdown"));
        Assert.Null(responseType.GetProperty("last6CategoryBreakdown"));
        Assert.Null(responseType.GetProperty("yearlyCategoryBreakdown"));
        Assert.Null(responseType.GetProperty("recentTransactions"));
        Assert.Null(responseType.GetProperty("availableYears"));

        var stats = responseType.GetProperty("stats")!.GetValue(response)!;
        Assert.Null(stats.GetType().GetProperty("pastThreeMonthsRewardsAverage"));
        Assert.Null(stats.GetType().GetProperty("hasRewardsHistory"));
        Assert.Null(stats.GetType().GetProperty("essentialsPercentRemaining"));

        var monthlyBreakdown = GetBreakdown(response, "monthlyCategoryBreakdown");
        Assert.Equal(new[] { ("Food", 100m) }, monthlyBreakdown);

        var setting = context.FinancialSettings.Single();
        Assert.Equal("Jul", setting.SelectedMonth);
        Assert.Equal(2026, setting.SelectedYear);
    }

    [Fact]
    public async Task GetDashboardDataAsync_ExcludesLegacyNegativeTransfersFromExpenses()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.Transactions.AddRange(
            Tx("food", 2026, 7, 9, "Food", -100m),
            Tx("legacy-transfer", 2026, 7, 10, "Transfer", -50m, "Transfer:Rewards->Growth"));
        await context.SaveChangesAsync();
        var service = NewService(context);

        var response = await service.GetDashboardDataAsync("Jul", 2026);

        Assert.Equal(new[] { ("Food", 100m) }, GetBreakdown(response, "monthlyCategoryBreakdown"));
        var stats = response.GetType().GetProperty("stats")!.GetValue(response)!;
        var expensesRaw = (string)stats.GetType().GetProperty("monthlyExpenses")!.GetValue(stats)!;
        Assert.Equal(100m, ObfuscationHelper.Deobfuscate(expensesRaw));
    }

    [Fact]
    public async Task GetDashboardDataAsync_OnlyIncludesAnnualPaymentInItsBillingMonth()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "annual",
            Name = "Insurance",
            Amount = -120m,
            Frequency = "Annually",
            Category = "Bills",
            LedgerCategory = "Essentials",
            StartDate = "2024-02-20",
            NextDueDate = "2024-02-20",
            DueDate = 20,
            Active = true
        });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var january = await service.GetDashboardDataAsync("Jan", 2026);
        var february = await service.GetDashboardDataAsync("Feb", 2026);

        Assert.Empty(GetObjects(january, "activeRecurringPayments"));
        Assert.Single(GetObjects(february, "activeRecurringPayments"));
        Assert.Equal(10m, GetStat(january, "activeRecurringTotal"));
        Assert.Equal(10m, GetStat(february, "activeRecurringTotal"));
    }

    [Fact]
    public async Task GetDashboardDataAsync_ExcludesExpiredPaymentsFromMonthlyRecurringTotal()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "expired",
            Name = "Old subscription",
            Amount = -30m,
            Frequency = "Monthly",
            Category = "Bills",
            LedgerCategory = "Essentials",
            StartDate = "2025-01-01",
            EndDate = "2025-12-31",
            NextDueDate = "2025-12-01",
            DueDate = 1,
            Active = true
        });
        await context.SaveChangesAsync();

        var response = await NewService(context).GetDashboardDataAsync("Jul", 2026);

        Assert.Equal(0m, GetStat(response, "activeRecurringTotal"));
    }

    private static (string category, decimal amount)[] GetBreakdown(object response, string propertyName)
    {
        var raw = (System.Collections.IEnumerable)response.GetType().GetProperty(propertyName)!.GetValue(response)!;
        var result = new List<(string category, decimal amount)>();
        foreach (var item in raw)
        {
            var category = (string)item.GetType().GetProperty("category")!.GetValue(item)!;
            var amountRaw = item.GetType().GetProperty("amount")!.GetValue(item) as string;
            var amount = ObfuscationHelper.Deobfuscate(amountRaw!);
            result.Add((category, amount));
        }
        return result.ToArray();
    }

    private static object[] GetObjects(object response, string propertyName) =>
        ((System.Collections.IEnumerable)response.GetType().GetProperty(propertyName)!.GetValue(response)!)
        .Cast<object>()
        .ToArray();

    private static decimal GetStat(object response, string propertyName)
    {
        var stats = response.GetType().GetProperty("stats")!.GetValue(response)!;
        var raw = (string)stats.GetType().GetProperty(propertyName)!.GetValue(stats)!;
        return ObfuscationHelper.Deobfuscate(raw);
    }

    private static FinancialService NewService(AppDbContext context)
    {
        var occurrences = new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        return new FinancialService(
            context,
            new CycleBalanceService(context),
            new RecurringPaymentAlertService(context, occurrences),
            occurrences);
    }
}
