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
            new[] { ("Growth", 1000000m), ("Food", 350m), ("Entertainment", 20m), ("Software", 15m), ("Hobbies", 5m) },
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
    public async Task GetDashboardDataAsync_ExcludesAdjustmentsAndMergesCategoryCasingEverywhere()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.Transactions.AddRange(
            Tx("food-1", 2026, 7, 2, "Food", -40m),
            Tx("food-2", 2026, 7, 3, "food", -10m),
            Tx("refund", 2026, 7, 4, "Food", 5m),
            Tx("adjust-down", 2026, 7, 5, "adjustment", -200m),
            Tx("adjust-up", 2026, 7, 6, "Adjustment", 300m),
            Tx("zero", 2026, 7, 7, "Food", 0m),
            Tx("discarded", 2026, 7, 8, "Food", -500m, "Discarded"));
        await context.SaveChangesAsync();

        var response = await NewService(context).GetDashboardDataAsync("Jul", 2026);

        Assert.Equal(5m, GetStat(response, "monthlyInflow"));
        Assert.Equal(50m, GetStat(response, "monthlyExpenses"));
        Assert.Equal(new[] { ("Food", 50m) }, GetBreakdown(response, "monthlyCategoryBreakdown"));
        Assert.Equal(50m, GetCategoryAmount(GetObjects(response, "categories"), "Essentials", "spent"));
    }

    [Fact]
    public async Task GetDashboardDataAsync_SeparatesPlannedTargetFromActualRecoveryAllocation()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1,
            EssentialsAlloc = .5m,
            GrowthAlloc = .2m,
            StabilityAlloc = .2m,
            RewardsAlloc = .1m
        });
        context.Transactions.Add(Tx("salary", 2026, 7, 2, "Salary", 1000m, "IncomeSplit:40,20,30,10"));
        await context.SaveChangesAsync();

        var categories = GetObjects(await NewService(context).GetDashboardDataAsync("Jul", 2026), "categories");

        Assert.Equal(500m, GetCategoryAmount(categories, "Essentials", "target"));
        Assert.Equal(400m, GetCategoryAmount(categories, "Essentials", "incomeAllocated"));
        Assert.Equal(300m, GetCategoryAmount(categories, "Stability", "incomeAllocated"));
    }

    [Fact]
    public async Task GetDashboardDataAsync_RollingGrowthTrendCrossesYearAndCarriesStableKeys()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        for (var month = 8; month <= 12; month++)
        {
            context.Transactions.Add(Tx($"growth-2026-{month}", 2026, month, 2, "Deposit", 10m, "Growth"));
        }
        context.Transactions.Add(Tx("growth-2027-1", 2027, 1, 2, "Deposit", 10m, "Growth"));
        await context.SaveChangesAsync();

        var response = await NewService(context, new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero))
            .GetDashboardDataAsync("Jan", 2027);

        var points = GetObjects(response, "last6TrendPoints");
        Assert.Equal(
            new[] { "2026-08", "2026-09", "2026-10", "2026-11", "2026-12", "2027-01" },
            points.Select(point => (string)point.GetType().GetProperty("cycleKey")!.GetValue(point)!).ToArray());
    }

    [Fact]
    public async Task GetDashboardDataAsync_SplitsOddCycleVelocityWithExtraDayInFirstHalf()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.Transactions.AddRange(
            Tx("day-16", 2026, 7, 16, "Food", -10m),
            Tx("day-17", 2026, 7, 17, "Food", -20m));
        await context.SaveChangesAsync();

        var response = await NewService(context).GetDashboardDataAsync("Jul", 2026);
        var insights = response.GetType().GetProperty("cycleSummaryInsights")!.GetValue(response)!;

        Assert.Equal(10m, GetAmount(insights, "velocityFirstHalf"));
        Assert.Equal(20m, GetAmount(insights, "velocitySecondHalf"));
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
            RecurringPaymentId = "streaming",
            RecurringOccurrenceDate = new DateOnly(2026, 7, 10)
        });
        // The occurrence was snapshotted when it settled, before the template was renamed and
        // repriced. The dashboard must read that snapshot, not the live template.
        context.RecurringPaymentOccurrences.Add(new RecurringPaymentOccurrence
        {
            Id = "occ-streaming-20260710",
            RecurringPaymentId = "streaming",
            OccurrenceDate = new DateOnly(2026, 7, 10),
            Name = "Original Plan",
            ScheduledAmount = 19m,
            Category = "Entertainment",
            LedgerCategory = "Rewards",
            PaymentMode = RecurringPaymentMode.Manual,
            Status = RecurringOccurrenceStatus.Paid,
            PaidDate = new DateOnly(2026, 7, 10),
            SettlementTransactionId = "streaming-jul-payment"
        });
        await context.SaveChangesAsync();

        var response = await NewService(context, new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero))
            .GetDashboardDataAsync("Jul", 2026);
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
    public async Task GetDashboardDataAsync_DoesNotSettleOccurrenceWithDifferentTaggedDate()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "streaming",
            Name = "Streaming Plan",
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
            Id = "streaming-next-occurrence",
            Date = new DateTime(2026, 7, 10),
            Description = "Streaming Plan",
            Amount = -25m,
            Category = "Software",
            LedgerCategory = "Essentials",
            RecurringPaymentId = "streaming",
            RecurringOccurrenceDate = new DateOnly(2026, 8, 10)
        });
        await context.SaveChangesAsync();

        var response = await NewService(context).GetDashboardDataAsync("Jul", 2026);
        var occurrence = Assert.Single(GetObjects(response, "activeRecurringPayments"));

        Assert.Equal("Pending", occurrence.GetType().GetProperty("status")!.GetValue(occurrence));
        Assert.False((bool)occurrence.GetType().GetProperty("isPaid")!.GetValue(occurrence)!);
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
        // Fixed clock: viewing June first materialises every occurrence up to "today", so a
        // drifting today would change which cycles this exercises.
        var service = NewService(context, new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));

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
    public async Task GetDashboardDataAsync_PausedBillStillShownButExcludedFromUnpaidTotals()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "paused-internet",
            Name = "Paused internet",
            Amount = -60m,
            Frequency = "Monthly",
            Category = "Bills",
            LedgerCategory = "Essentials",
            StartDate = "2026-07-20",
            NextDueDate = "2026-07-20",
            DueDate = 20,
            Active = false
        });
        context.RecurringPaymentOccurrences.Add(new RecurringPaymentOccurrence
        {
            Id = "occ-paused-internet-20260720",
            RecurringPaymentId = "paused-internet",
            OccurrenceDate = new DateOnly(2026, 7, 20),
            Name = "Paused internet",
            ScheduledAmount = 60m,
            Category = "Bills",
            LedgerCategory = "Essentials",
            Status = RecurringOccurrenceStatus.Pending,
            UserId = TestHelpers.DefaultUserId
        });
        await context.SaveChangesAsync();

        var response = await NewService(context, new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero))
            .GetDashboardDataAsync("Jul", 2026);
        var insights = response.GetType().GetProperty("todayPlanInsights")!.GetValue(response)!;

        Assert.Single(GetObjects(response, "activeRecurringPayments"));
        Assert.Equal(0, insights.GetType().GetProperty("unpaidRecurringCount")!.GetValue(insights));
        Assert.Equal(0m, GetAmount(insights, "unpaidRecurringTotal"));
    }

    [Fact]
    public async Task GetDashboardDataAsync_IncludesRecurringAccountShortfalls_WhenAccountBalanceIsBelowAutoDeductBill()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026 });
        var account = new LedgerAccount
        {
            Id = "acc-main",
            Name = "Checking Account",
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank
        };
        context.LedgerAccounts.Add(account);
        // Seed 300 balance on acc-main
        context.Transactions.Add(new Transaction
        {
            Id = "tx-open",
            Date = new DateTime(2026, 7, 1),
            Description = "Income",
            Category = "Income",
            LedgerCategory = "Essentials",
            AccountId = "acc-main",
            Amount = 300m
        });
        // Auto-deduct bill of 1200 on July 20 (shortfall = 900)
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "rec-rent",
            Name = "Rent",
            Amount = 1200m,
            Frequency = "Monthly",
            Category = "Bills",
            LedgerCategory = "Essentials",
            AccountId = "acc-main",
            PaymentMode = RecurringPaymentMode.AutoDeduct,
            StartDate = "2026-01-01",
            NextDueDate = "2026-07-20",
            DueDate = 20,
            Active = true
        });
        await context.SaveChangesAsync();

        var response = await NewService(context, new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero))
            .GetDashboardDataAsync("Jul", 2026);
        var shortfalls = ((IEnumerable<object>)response.GetType().GetProperty("recurringAccountShortfalls")!.GetValue(response)!)
            .ToList();

        Assert.Single(shortfalls);
        var shortfall = shortfalls[0];
        Assert.Equal("Rent", (string)shortfall.GetType().GetProperty("name")!.GetValue(shortfall)!);
        Assert.Equal(1200m, GetAmount(shortfall, "amount"));
        Assert.Equal(300m, GetAmount(shortfall, "accountBalance"));
        Assert.Equal(900m, GetAmount(shortfall, "shortfall"));
        Assert.Equal("Checking Account", (string)shortfall.GetType().GetProperty("accountName")!.GetValue(shortfall)!);
        Assert.Equal(1, (int)shortfall.GetType().GetProperty("offsetDays")!.GetValue(shortfall)!);
    }

    [Fact]
    public async Task GetDashboardDataAsync_UsesTheGuideEffectiveForTheSelectedCycle()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.TransactionCategories.Add(new TransactionCategory
        {
            Id = "cat-transport",
            Name = "Transport",
            Type = CategoryFlowType.Outflow
        });
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

    [Fact]
    public async Task GetDashboardDataAsync_ExcludesCurrentInflowCategoryGuides()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.TransactionCategories.Add(new TransactionCategory
        {
            Id = "cat-salary",
            Name = "Salary",
            Type = CategoryFlowType.Inflow,
            CycleLimit = 500m
        });
        context.CategorySpendingGuides.Add(new CategorySpendingGuide
        {
            Id = "guide-salary",
            CategoryName = "Salary",
            EffectiveFromCycleKey = "2026-07",
            LimitAmount = 500m
        });
        await context.SaveChangesAsync();

        var response = await NewService(context, new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero))
            .GetDashboardDataAsync("Jul", 2026);

        Assert.Empty(GetObjects(response, "categoryLimitProgress"));
    }

    [Fact]
    public async Task GetDashboardDataAsync_CarriesTheEmergencyFundRecoveryBlock()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            TargetStabilityFund = 10000m
        });
        context.Transactions.AddRange(
            Tx("built-up", 2026, 4, 10, "Other", 3000m, "Stability"),
            Tx("spent", 2026, 6, 10, "Other", -900m, "Stability"));
        await context.SaveChangesAsync();

        var response = await NewService(context, new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero))
            .GetDashboardDataAsync("Jul", 2026);

        var recovery = response.GetType().GetProperty("stabilityRecovery")!.GetValue(response)!;
        Assert.True((bool)recovery.GetType().GetProperty("IsActive")!.GetValue(recovery)!);
        Assert.Equal(900m, GetAmount(recovery, "OutstandingShortfall"));
        Assert.Equal("2026-06", recovery.GetType().GetProperty("LastDrawdownCycleKey")!.GetValue(recovery));
    }

    [Fact]
    public async Task GetDashboardDataAsync_LeavesTheRecoveryBlockInactiveForAFundThatNeverFell()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 1,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            TargetStabilityFund = 10000m
        });
        context.Transactions.Add(Tx("built-up", 2026, 4, 10, "Other", 3000m, "Stability"));
        await context.SaveChangesAsync();

        var response = await NewService(context, new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero))
            .GetDashboardDataAsync("Jul", 2026);

        var recovery = response.GetType().GetProperty("stabilityRecovery")!.GetValue(response)!;
        Assert.False((bool)recovery.GetType().GetProperty("IsActive")!.GetValue(recovery)!);
    }

    /// <summary>
    /// summaryOnly skips EnsureComputedThroughAsync, and reload state is only trustworthy once the
    /// cycle-balance cache is complete -- so that path reports nothing rather than a
    /// figure derived from a half-built cache.
    /// </summary>
    [Fact]
    public async Task GetDashboardDataAsync_OmitsTheRecoveryBlockUnderSummaryOnly()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1, SelectedMonth = "Jul", SelectedYear = 2026 });
        await context.SaveChangesAsync();

        var response = await NewService(context).GetDashboardDataAsync("Jul", 2026, summaryOnly: true);

        Assert.Null(response.GetType().GetProperty("stabilityRecovery")!.GetValue(response));
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
