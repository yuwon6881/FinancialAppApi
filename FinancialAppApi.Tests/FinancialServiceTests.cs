using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public class FinancialServiceTests
{
    [Fact]
    public async Task GetWalletBalanceAsync_ReturnsCurrentCycleWalletBalance()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 1 });
        context.Transactions.Add(new Transaction
        {
            Id = "tx-1",
            Date = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc),
            Description = "Reward",
            Category = "Other",
            LedgerCategory = "Rewards",
            Amount = 50m
        });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var response = await service.GetWalletBalanceAsync();

        var totalBalance = response.GetType().GetProperty("totalBalance")!.GetValue(response) as string;
        Assert.Equal(50m, ObfuscationHelper.Deobfuscate(totalBalance!));
    }

    [Fact]
    public async Task UpdateSettingsAsync_ClampsCycleDayAndInvalidatesCycleBalanceCache()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting { CycleDay = 28 });
        context.CycleBalances.Add(new CycleBalance
        {
            Year = 2026,
            MonthIndex = 7,
            EssentialsBalance = 1m,
            GrowthBalance = 2m,
            StabilityBalance = 3m,
            RewardsBalance = 4m
        });
        await context.SaveChangesAsync();
        var service = NewService(context);

        await service.UpdateSettingsAsync(new FinancialSettingsUpdate(
            ObfuscationHelper.Obfuscate(1234.56m),
            0.50m,
            0.25m,
            0.15m,
            0.10m,
            0,
            true,
            false,
            "USD",
            "Rewards"));

        var setting = context.FinancialSettings.Single();
        Assert.Equal(1, setting.CycleDay);
        Assert.True(setting.DarkMode);
        Assert.False(setting.HideSensitive);
        Assert.Empty(context.CycleBalances);
    }

    [Fact]
    public async Task UpdateSettingsAsync_TargetChangeInvalidatesCycleBalanceCache()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 28,
            TargetStabilityFund = 10000m,
            EssentialsAlloc = .5m,
            GrowthAlloc = .25m,
            StabilityAlloc = .15m,
            RewardsAlloc = .1m
        });
        context.CycleBalances.Add(new CycleBalance { Year = 2026, MonthIndex = 7 });
        await context.SaveChangesAsync();

        await NewService(context).UpdateSettingsAsync(new FinancialSettingsUpdate(
            ObfuscationHelper.Obfuscate(5000m), .5m, .25m, .15m, .1m, 28,
            null, null, "USD", null));

        Assert.Empty(context.CycleBalances);
    }

    [Fact]
    public async Task UpdateSettingsAsync_StabilityAllocationChangeInvalidatesCycleBalanceCache()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            CycleDay = 28,
            TargetStabilityFund = 10000m,
            EssentialsAlloc = .5m,
            GrowthAlloc = .25m,
            StabilityAlloc = .15m,
            RewardsAlloc = .1m
        });
        context.CycleBalances.Add(new CycleBalance { Year = 2026, MonthIndex = 7 });
        await context.SaveChangesAsync();

        await NewService(context).UpdateSettingsAsync(new FinancialSettingsUpdate(
            ObfuscationHelper.Obfuscate(10000m), .45m, .25m, .2m, .1m, 28,
            null, null, "USD", null));

        Assert.Empty(context.CycleBalances);
    }

    [Fact]
    public async Task UpdateSettingsAsync_RejectsAllocationsThatDoNotTotalOneHundredPercent()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        context.FinancialSettings.Add(new FinancialSetting
        {
            EssentialsAlloc = 0.50m,
            GrowthAlloc = 0.25m,
            StabilityAlloc = 0.15m,
            RewardsAlloc = 0.10m
        });
        await context.SaveChangesAsync();
        var service = NewService(context);

        var error = await service.UpdateSettingsAsync(new FinancialSettingsUpdate(
            ObfuscationHelper.Obfuscate(1234.56m),
            0.50m,
            0.25m,
            0.15m,
            0.20m,
            28,
            null,
            null,
            "USD",
            null));

        Assert.Equal("Income allocations must total exactly 100%.", error);
        Assert.Equal(0.10m, context.FinancialSettings.Single().RewardsAlloc);
    }

    private static FinancialService NewService(AppDbContext context)
    {
        var occurrences = new RecurringOccurrenceService(NullLogger<RecurringOccurrenceService>.Instance);
        var clock = new FinancialClock(
            TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero)));
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
