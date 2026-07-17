using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Database;

public static class DbSeeder
{
    private static readonly string[] DefaultCategoryNames =
    [
        "Salary", "Social", "Food", "Hobbies", "Software", "Investment",
        "Entertainment", "Transport", "Other", "Transfer"
    ];

    public static void Seed(AppDbContext context, FinancialClock? financialClock = null)
    {
        var clock = financialClock ?? FinancialClock.Utc;
        var userIds = context.AppUsers.AsNoTracking().Select(user => user.Id).ToList();
        foreach (var userId in userIds)
        {
            EnsureUserDefaults(context, userId, clock);
        }

        context.SaveChanges();
    }

    public static void EnsureUserDefaults(
        AppDbContext context,
        string userId,
        FinancialClock? financialClock = null)
    {
        var clock = financialClock ?? FinancialClock.Utc;
        if (!context.FinancialSettings.IgnoreQueryFilters().Any(setting => setting.UserId == userId))
        {
            var now = clock.LocalNow;
            context.FinancialSettings.Add(new FinancialSetting
            {
                UserId = userId,
                TargetStabilityFund = 10000.00m,
                SelectedMonth = now.ToString("MMM"),
                SelectedYear = now.Year,
                EssentialsAlloc = 0.50m,
                GrowthAlloc = 0.25m,
                StabilityAlloc = 0.15m,
                RewardsAlloc = 0.10m,
                CycleDay = 28,
                HideSensitive = true,
                VibrationEnabled = true
            });
        }

        var existingNames = context.TransactionCategories
            .IgnoreQueryFilters()
            .Where(category => category.UserId == userId)
            .Select(category => category.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < DefaultCategoryNames.Length; index++)
        {
            var name = DefaultCategoryNames[index];
            if (existingNames.Contains(name)) continue;

            context.TransactionCategories.Add(new TransactionCategory
            {
                Id = $"cat-{userId}-{index + 1}",
                UserId = userId,
                Name = name
            });
        }
    }
}
