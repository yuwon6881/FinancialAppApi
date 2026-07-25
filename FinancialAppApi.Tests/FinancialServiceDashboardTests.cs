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
    public async Task GetDashboardDataAsync_CanReadHistoricalCycleWithoutChangingSelectedPeriod()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jan", SelectedYear = 2025 });
        await context.SaveChangesAsync();

        await NewService(context).GetDashboardDataAsync("Jul", 2026, persistSelection: false);

        var setting = context.FinancialSettings.Single();
        Assert.Equal("Jan", setting.SelectedMonth);
        Assert.Equal(2025, setting.SelectedYear);
    }

    [Fact]
    public async Task GetDashboardDataAsync_SummaryOnlyOmitsUnneededDashboardExtras()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.Transactions.Add(Tx("jul", 2026, 7, 9, "Food", -100m));
        await context.SaveChangesAsync();

        var response = await NewService(context).GetDashboardDataAsync("Jul", 2026, persistSelection: false, summaryOnly: true);

        Assert.Empty(GetObjects(response, "trendPoints"));
        Assert.Empty(GetObjects(response, "last3TrendPoints"));
        Assert.Empty(GetObjects(response, "last6TrendPoints"));
        Assert.Empty(GetObjects(response, "pendingNotifications"));
        Assert.Equal(new[] { ("Food", 100m) }, GetBreakdown(response, "monthlyCategoryBreakdown"));
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
    public async Task GetDashboardDataAsync_ReportsTrueEnvelopeSpendingWithoutIncomeOrTransfers()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.Transactions.AddRange(
            Tx("income", 2026, 7, 1, "Salary", 1000m, "IncomeSplit:50,20,10,20"),
            Tx("food", 2026, 7, 9, "Food", -125m, "Essentials"),
            Tx("course", 2026, 7, 10, "Education", -40m, "Growth"),
            Tx("treat", 2026, 7, 11, "Hobbies", -25m, "Rewards"),
            Tx("transfer", 2026, 7, 12, "Transfer", -75m, "Transfer:Essentials->Rewards"));
        await context.SaveChangesAsync();

        var response = await NewService(context).GetDashboardDataAsync("Jul", 2026);
        var categories = GetObjects(response, "categories");

        Assert.Equal(125m, GetCategoryAmount(categories, "Essentials", "spent"));
        Assert.Equal(40m, GetCategoryAmount(categories, "Growth", "spent"));
        Assert.Equal(0m, GetCategoryAmount(categories, "Stability", "spent"));
        Assert.Equal(25m, GetCategoryAmount(categories, "Rewards", "spent"));
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

    [Fact]
    public async Task GetDashboardDataAsync_PaidOccurrenceUsesLedgerSnapshotAfterTemplateChanges()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "streaming",
            Name = "Renamed Future Plan",
            Amount = -25m,
            Frequency = "Monthly",
            Category = "Software",
            LedgerCategory = "Essentials",
            StartDate = "2026-01-10",
            NextDueDate = "2026-01-10",
            DueDate = 10,
            Active = true
        });
        context.Transactions.Add(new Transaction
        {
            Id = "streaming-jul-payment",
            Date = new DateTime(2026, 7, 10),
            Description = "Original Plan",
            Amount = -19m,
            Category = "Entertainment",
            LedgerCategory = "Rewards",
            RecurringPaymentId = "streaming"
        });
        await context.SaveChangesAsync();

        var response = await NewService(context).GetDashboardDataAsync("Jul", 2026);
        var occurrence = Assert.Single(GetObjects(response, "activeRecurringPayments"));
        var occurrenceType = occurrence.GetType();

        Assert.Equal("Original Plan", occurrenceType.GetProperty("name")!.GetValue(occurrence));
        Assert.Equal("Entertainment", occurrenceType.GetProperty("category")!.GetValue(occurrence));
        Assert.Equal("Rewards", occurrenceType.GetProperty("ledgerCategory")!.GetValue(occurrence));
        Assert.Equal("Paid", occurrenceType.GetProperty("status")!.GetValue(occurrence));
        var amountRaw = (string)occurrenceType.GetProperty("amount")!.GetValue(occurrence)!;
        Assert.Equal(19m, ObfuscationHelper.Deobfuscate(amountRaw));
    }

    [Fact]
    public async Task GetDashboardDataAsync_KeepsPaidOccurrenceAfterSubscriptionIsDeleted()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.Transactions.Add(new Transaction
        {
            Id = "deleted-streaming-jul-payment",
            Date = new DateTime(2026, 7, 10),
            Description = "Streaming service",
            Amount = -19m,
            Category = "Entertainment",
            LedgerCategory = "Essentials",
            RecurringPaymentId = "deleted-streaming",
            RecurringOccurrenceDate = new DateOnly(2026, 7, 10)
        });
        await context.SaveChangesAsync();

        var response = await NewService(context).GetDashboardDataAsync("Jul", 2026);
        var occurrence = Assert.Single(GetObjects(response, "activeRecurringPayments"));
        var occurrenceType = occurrence.GetType();

        Assert.Equal("deleted-streaming", occurrenceType.GetProperty("recurringPaymentId")!.GetValue(occurrence));
        Assert.Equal("Streaming service", occurrenceType.GetProperty("name")!.GetValue(occurrence));
        Assert.Equal("Paid", occurrenceType.GetProperty("status")!.GetValue(occurrence));
        Assert.Equal("2026-07-10", occurrenceType.GetProperty("dueDate")!.GetValue(occurrence));
        Assert.Equal(19m, ObfuscationHelper.Deobfuscate((string)occurrenceType.GetProperty("amount")!.GetValue(occurrence)!));
    }

    [Fact]
    public async Task GetDashboardDataAsync_PayEarlyForNextCycleDoesNotOverturnThisCyclesDiscard()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "household",
            Name = "Household",
            Amount = -870m,
            Frequency = "Monthly",
            Category = "Bills",
            LedgerCategory = "Essentials",
            StartDate = "2026-01-28",
            NextDueDate = "2026-06-28",
            DueDate = 28,
            Active = true
        });
        // The June occurrence was discarded on its due date...
        context.Transactions.Add(new Transaction
        {
            Id = "household-jun-discard",
            Date = new DateTime(2026, 6, 28),
            Description = "[Discarded] Household",
            Amount = 0,
            Category = "Bills",
            LedgerCategory = "Discarded",
            RecurringPaymentId = "household",
            RecurringOccurrenceDate = new DateOnly(2026, 6, 28)
        });
        // ...then, two days later (still within the June cycle), the user paid the next
        // (July) occurrence early. Its posting date falls in June, but it settles July.
        context.Transactions.Add(new Transaction
        {
            Id = "household-payearly",
            Date = new DateTime(2026, 6, 30),
            Description = "Household",
            Amount = -870m,
            Category = "Bills",
            LedgerCategory = "Essentials",
            RecurringPaymentId = "household",
            RecurringOccurrenceDate = new DateOnly(2026, 7, 28)
        });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var june = await service.GetDashboardDataAsync("Jun", 2026);
        var july = await service.GetDashboardDataAsync("Jul", 2026);

        var juneOccurrence = Assert.Single(GetObjects(june, "activeRecurringPayments"));
        Assert.Equal("Discarded", juneOccurrence.GetType().GetProperty("status")!.GetValue(juneOccurrence));

        var julyOccurrence = Assert.Single(GetObjects(july, "activeRecurringPayments"));
        Assert.Equal("Paid", julyOccurrence.GetType().GetProperty("status")!.GetValue(julyOccurrence));
    }

    [Fact]
    public async Task GetDashboardDataAsync_ComputesTodayPlanInsightsFromUnpaidBillsAndCurrentPace()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.Transactions.Add(Tx("food", 2026, 7, 3, "Food", -150m));
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "internet",
            Name = "Internet",
            Amount = -60m,
            Frequency = "Monthly",
            Category = "Bills",
            LedgerCategory = "Essentials",
            StartDate = "2026-01-20",
            NextDueDate = "2026-01-20",
            DueDate = 20,
            Active = true
        });
        await context.SaveChangesAsync();

        var response = await NewService(context, new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero))
            .GetDashboardDataAsync("Jul", 2026);
        var insights = response.GetType().GetProperty("todayPlanInsights")!.GetValue(response)!;

        Assert.Equal(1, insights.GetType().GetProperty("unpaidRecurringCount")!.GetValue(insights));
        Assert.Equal(60m, GetAmount(insights, "unpaidRecurringTotal"));
        Assert.Equal(150m, GetAmount(insights, "nonRecurringEssentialsSpent"));
        Assert.Equal(9.38m, GetAmount(insights, "nonRecurringEssentialsDailyAverage"));
        Assert.Equal(-350.63m, GetAmount(insights, "projectedEssentialsEndingBalance"));
    }

    [Fact]
    public async Task GetDashboardDataAsync_UsesTheGuideEffectiveForTheSelectedCycle()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.Transactions.Add(Tx("transport", 2026, 7, 4, "Transport", -150m));
        context.CategorySpendingGuides.AddRange(
            new CategorySpendingGuide
            {
                Id = "guide-old",
                CategoryName = "Transport",
                EffectiveFromCycleKey = "2026-06",
                LimitAmount = 100m
            },
            new CategorySpendingGuide
            {
                Id = "guide-current",
                CategoryName = "Transport",
                EffectiveFromCycleKey = "2026-07",
                LimitAmount = 400m
            },
            new CategorySpendingGuide
            {
                Id = "guide-future",
                CategoryName = "Transport",
                EffectiveFromCycleKey = "2026-08",
                LimitAmount = 50m
            });
        await context.SaveChangesAsync();

        var response = await NewService(context, new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero))
            .GetDashboardDataAsync("Jul", 2026);
        var progress = Assert.Single(GetObjects(response, "categoryLimitProgress"));

        Assert.Equal("Transport", progress.GetType().GetProperty("category")!.GetValue(progress));
        Assert.Equal(400m, GetAmount(progress, "limit"));
        Assert.Equal(150m, GetAmount(progress, "spent"));
        Assert.Equal(290.63m, GetAmount(progress, "projectedSpend"));
        Assert.Equal("OnTrack", progress.GetType().GetProperty("status")!.GetValue(progress));
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

    private static decimal GetCategoryAmount(object[] categories, string name, string propertyName)
    {
        var category = categories.Single(item =>
            string.Equals((string)item.GetType().GetProperty("name")!.GetValue(item)!, name, StringComparison.Ordinal));
        var raw = (string)category.GetType().GetProperty(propertyName)!.GetValue(category)!;
        return ObfuscationHelper.Deobfuscate(raw);
    }

    private static decimal GetAmount(object value, string propertyName)
    {
        var raw = (string)value.GetType().GetProperty(propertyName)!.GetValue(value)!;
        return ObfuscationHelper.Deobfuscate(raw);
    }

    private static FinancialService NewService(AppDbContext context, DateTimeOffset? utcNow = null)
    {
        var occurrences = new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        var clock = utcNow.HasValue
            ? new FinancialClock(
                TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")),
                new FixedTimeProvider(utcNow.Value))
            : null;
        return new FinancialService(
            context,
            new CycleBalanceService(context),
            new RecurringPaymentAlertService(context, occurrences, clock),
            occurrences,
            clock);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
