using FinancialAppApi.Models;

namespace FinancialAppApi.Database;

public static class DbSeeder
{
    private static readonly TransactionCategory[] DefaultCategories =
    [
        new() { Id = "cat-1", Name = "Salary" },
        new() { Id = "cat-2", Name = "Social" },
        new() { Id = "cat-3", Name = "Food" },
        new() { Id = "cat-4", Name = "Hobbies" },
        new() { Id = "cat-5", Name = "Software" },
        new() { Id = "cat-6", Name = "Investment" },
        new() { Id = "cat-7", Name = "Entertainment" },
        new() { Id = "cat-8", Name = "Transport" },
        new() { Id = "cat-9", Name = "Other" },
        new() { Id = "cat-10", Name = "Transfer" }
    ];

    public static void Seed(AppDbContext context)
    {
        SeedTransactionCategories(context);
        SeedFinancialSettings(context);
    }

    private static void SeedTransactionCategories(AppDbContext context)
    {
        var existingNames = context.TransactionCategories
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingCategories = DefaultCategories
            .Where(category => !existingNames.Contains(category.Name))
            .ToArray();

        if (missingCategories.Length == 0)
        {
            return;
        }

        context.TransactionCategories.AddRange(missingCategories);
        context.SaveChanges();
    }

    private static void SeedFinancialSettings(AppDbContext context)
    {
        if (context.FinancialSettings.Any())
        {
            return;
        }

        var now = DateTime.Now;
        context.FinancialSettings.Add(new FinancialSetting
        {
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
        context.SaveChanges();
    }
}
