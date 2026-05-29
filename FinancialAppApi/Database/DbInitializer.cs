using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using FinancialAppApi.Models;

namespace FinancialAppApi.Database;

public static class DbInitializer
{
    public static void Initialize(AppDbContext context)
    {
        // Ensure database is created and schema is updated
        var databaseCreator = context.Database.GetService<IDatabaseCreator>() as IRelationalDatabaseCreator;
        if (databaseCreator != null)
        {
            try
            {
                databaseCreator.CreateTables();
            }
            catch (Exception)
            {
                // Ignore if tables already exist (e.g. on subsequent runs)
            }
        }
        else
        {
            context.Database.EnsureCreated();
        }

        var isPostgres = context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ?? false;

        // Try to drop the MonthlyIncome column if it exists in SQLite/PostgreSQL database table
        try
        {
            if (isPostgres)
            {
                context.Database.ExecuteSqlRaw("ALTER TABLE \"FinancialSettings\" DROP COLUMN IF EXISTS \"MonthlyIncome\";");
            }
            else
            {
                context.Database.ExecuteSqlRaw("ALTER TABLE [FinancialSettings] DROP COLUMN [MonthlyIncome];");
            }
        }
        catch (Exception)
        {
            // Ignore if column already dropped or not supported
        }

        // Add DarkMode column if it does not yet exist
        try
        {
            if (isPostgres)
            {
                context.Database.ExecuteSqlRaw("ALTER TABLE \"FinancialSettings\" ADD COLUMN \"DarkMode\" boolean NOT NULL DEFAULT FALSE;");
            }
            else
            {
                context.Database.ExecuteSqlRaw("ALTER TABLE [FinancialSettings] ADD COLUMN [DarkMode] INTEGER NOT NULL DEFAULT 0;");
            }
        }
        catch (Exception)
        {
            // Ignore if column already exists
        }

        // Clean up: drop the unused RecurringPaymentDismissals table
        try
        {
            if (isPostgres)
            {
                context.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS \"RecurringPaymentDismissals\";");
            }
            else
            {
                context.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS [RecurringPaymentDismissals];");
            }
        }
        catch (Exception)
        {
            // Ignore
        }


        // 1. Seed Transaction Categories if empty
        if (!context.TransactionCategories.Any())
        {
            var categories = new TransactionCategory[]
            {
                new() { Id = "cat-1", Name = "Salary" },
                new() { Id = "cat-2", Name = "Social" },
                new() { Id = "cat-3", Name = "Food" },
                new() { Id = "cat-4", Name = "Hobbies" },
                new() { Id = "cat-5", Name = "Software" },
                new() { Id = "cat-6", Name = "Investment" },
                new() { Id = "cat-7", Name = "Entertainment" },
                new() { Id = "cat-8", Name = "Transport" },
                new() { Id = "cat-9", Name = "Other" }
            };

            context.TransactionCategories.AddRange(categories);
            context.SaveChanges();
        }

        // 2. Seed Financial Settings if empty
        if (!context.FinancialSettings.Any())
        {
            var now = DateTime.Now;
            var settings = new FinancialSetting
            {
                TargetStabilityFund = 10000.00m,
                SelectedMonth = now.ToString("MMM"), // e.g., "May"
                SelectedYear = now.Year,             // e.g., 2026
                EssentialsAlloc = 0.50m,
                GrowthAlloc = 0.25m,
                StabilityAlloc = 0.15m,
                RewardsAlloc = 0.10m,
                CycleDay = 28
            };

            context.FinancialSettings.Add(settings);
            context.SaveChanges();
        }
    }
}
