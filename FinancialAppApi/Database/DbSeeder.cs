using FinancialAppApi.Models;

namespace FinancialAppApi.Database;

public static class DbSeeder
{
    public static void Seed(AppDbContext context)
    {
        SeedFinancialSettings(context);
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
