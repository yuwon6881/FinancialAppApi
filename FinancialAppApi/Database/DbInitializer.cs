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
                if (ColumnExists(context, "FinancialSettings", "MonthlyIncome", isPostgres))
                {
                    context.Database.ExecuteSqlRaw("ALTER TABLE \"FinancialSettings\" DROP COLUMN IF EXISTS \"MonthlyIncome\";");
                }
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
                if (!ColumnExists(context, "FinancialSettings", "DarkMode", isPostgres))
                {
                    context.Database.ExecuteSqlRaw("ALTER TABLE \"FinancialSettings\" ADD COLUMN \"DarkMode\" boolean NOT NULL DEFAULT FALSE;");
                }
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

        // Add Currency column if it does not yet exist
        try
        {
            if (isPostgres)
            {
                if (!ColumnExists(context, "FinancialSettings", "Currency", isPostgres))
                {
                    context.Database.ExecuteSqlRaw("ALTER TABLE \"FinancialSettings\" ADD COLUMN \"Currency\" text NOT NULL DEFAULT 'USD';");
                }
            }
            else
            {
                context.Database.ExecuteSqlRaw("ALTER TABLE [FinancialSettings] ADD COLUMN [Currency] TEXT NOT NULL DEFAULT 'USD';");
            }
        }
        catch (Exception)
        {
            // Ignore if column already exists
        }

        // Add HideSensitive column if it does not yet exist
        try
        {
            if (isPostgres)
            {
                if (!ColumnExists(context, "FinancialSettings", "HideSensitive", isPostgres))
                {
                    context.Database.ExecuteSqlRaw("ALTER TABLE \"FinancialSettings\" ADD COLUMN \"HideSensitive\" boolean NOT NULL DEFAULT TRUE;");
                }
            }
            else
            {
                context.Database.ExecuteSqlRaw("ALTER TABLE [FinancialSettings] ADD COLUMN [HideSensitive] INTEGER NOT NULL DEFAULT 1;");
            }
        }
        catch (Exception)
        {
            // Ignore if column already exists
        }

        // Add VibrationEnabled column if it does not yet exist
        try
        {
            if (isPostgres)
            {
                if (!ColumnExists(context, "FinancialSettings", "VibrationEnabled", isPostgres))
                {
                    context.Database.ExecuteSqlRaw("ALTER TABLE \"FinancialSettings\" ADD COLUMN \"VibrationEnabled\" boolean NOT NULL DEFAULT TRUE;");
                }
            }
            else
            {
                context.Database.ExecuteSqlRaw("ALTER TABLE [FinancialSettings] ADD COLUMN [VibrationEnabled] INTEGER NOT NULL DEFAULT 1;");
            }
        }
        catch (Exception)
        {
            // Ignore if column already exists
        }
        try
        {
            if (isPostgres)
            {
                if (!ColumnExists(context, "FinancialSettings", "StabilityOverflowRedirect", isPostgres))
                {
                    context.Database.ExecuteSqlRaw("ALTER TABLE \"FinancialSettings\" ADD COLUMN \"StabilityOverflowRedirect\" text NOT NULL DEFAULT 'Split: Growth 50%, Rewards 50%';");
                }
            }
            else
            {
                context.Database.ExecuteSqlRaw("ALTER TABLE [FinancialSettings] ADD COLUMN [StabilityOverflowRedirect] TEXT NOT NULL DEFAULT 'Split: Growth 50%, Rewards 50%';");
            }
        }
        catch (Exception)
        {
            // Ignore if column already exists
        }

        // Add RecurringPaymentId column to Transactions if it does not yet exist
        try
        {
            if (isPostgres)
            {
                if (!ColumnExists(context, "Transactions", "RecurringPaymentId", isPostgres))
                {
                    context.Database.ExecuteSqlRaw("ALTER TABLE \"Transactions\" ADD COLUMN \"RecurringPaymentId\" text;");
                }
            }
            else
            {
                context.Database.ExecuteSqlRaw("ALTER TABLE [Transactions] ADD COLUMN [RecurringPaymentId] TEXT;");
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
                if (TableExists(context, "RecurringPaymentDismissals", isPostgres))
                {
                    context.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS \"RecurringPaymentDismissals\";");
                }
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

        // Create WishlistItems table if it doesn't exist
        try
        {
            if (isPostgres)
            {
                if (!TableExists(context, "WishlistItems", isPostgres))
                {
                    context.Database.ExecuteSqlRaw(@"
                        CREATE TABLE IF NOT EXISTS ""WishlistItems"" (
                            ""Id"" SERIAL PRIMARY KEY,
                            ""Name"" TEXT NOT NULL,
                            ""Price"" NUMERIC NOT NULL,
                            ""Priority"" TEXT NOT NULL DEFAULT 'Medium',
                            ""IsPurchased"" BOOLEAN NOT NULL DEFAULT FALSE,
                            ""PurchasedAt"" TIMESTAMP,
                            ""CreatedAt"" TIMESTAMP NOT NULL DEFAULT NOW(),
                            ""IsActive"" BOOLEAN NOT NULL DEFAULT FALSE
                        );");
                }
            }
            else
            {
                context.Database.ExecuteSqlRaw(@"
                    CREATE TABLE IF NOT EXISTS [WishlistItems] (
                        [Id] INTEGER PRIMARY KEY AUTOINCREMENT,
                        [Name] TEXT NOT NULL,
                        [Price] TEXT NOT NULL,
                        [Priority] TEXT NOT NULL DEFAULT 'Medium',
                        [IsPurchased] INTEGER NOT NULL DEFAULT 0,
                        [PurchasedAt] TEXT,
                        [CreatedAt] TEXT NOT NULL,
                        [IsActive] INTEGER NOT NULL DEFAULT 0
                    );");
            }
        }
        catch (Exception)
        {
            // Ignore
        }

        // Drop the old non-cryptographic "biometric" credential table (no server-side
        // signature verification - replaced by real WebAuthn credentials below).
        try
        {
            if (isPostgres)
            {
                if (TableExists(context, "BiometricCredentials", isPostgres))
                {
                    context.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS \"BiometricCredentials\";");
                }
            }
            else
            {
                context.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS [BiometricCredentials];");
            }
        }
        catch (Exception)
        {
            // Ignore
        }

        // Create WebAuthnCredentials/WebAuthnChallenges tables if they don't exist
        try
        {
            if (isPostgres)
            {
                if (!TableExists(context, "WebAuthnCredentials", isPostgres))
                {
                    context.Database.ExecuteSqlRaw(@"
                        CREATE TABLE IF NOT EXISTS ""WebAuthnCredentials"" (
                            ""CredentialId"" BYTEA PRIMARY KEY,
                            ""Username"" TEXT NOT NULL,
                            ""PublicKey"" BYTEA NOT NULL,
                            ""SignCount"" BIGINT NOT NULL,
                            ""DeviceLabel"" TEXT,
                            ""CreatedAt"" TIMESTAMP NOT NULL
                        );");
                }
                if (!TableExists(context, "WebAuthnChallenges", isPostgres))
                {
                    context.Database.ExecuteSqlRaw(@"
                        CREATE TABLE IF NOT EXISTS ""WebAuthnChallenges"" (
                            ""Id"" TEXT PRIMARY KEY,
                            ""Purpose"" TEXT NOT NULL,
                            ""Username"" TEXT NOT NULL,
                            ""OptionsJson"" TEXT NOT NULL,
                            ""ExpiresAt"" TIMESTAMP NOT NULL
                        );");
                }
            }
            else
            {
                context.Database.ExecuteSqlRaw(@"
                    CREATE TABLE IF NOT EXISTS [WebAuthnCredentials] (
                        [CredentialId] BLOB PRIMARY KEY,
                        [Username] TEXT NOT NULL,
                        [PublicKey] BLOB NOT NULL,
                        [SignCount] INTEGER NOT NULL,
                        [DeviceLabel] TEXT,
                        [CreatedAt] TEXT NOT NULL
                    );");
                context.Database.ExecuteSqlRaw(@"
                    CREATE TABLE IF NOT EXISTS [WebAuthnChallenges] (
                        [Id] TEXT PRIMARY KEY,
                        [Purpose] TEXT NOT NULL,
                        [Username] TEXT NOT NULL,
                        [OptionsJson] TEXT NOT NULL,
                        [ExpiresAt] TEXT NOT NULL
                    );");
            }
        }
        catch (Exception)
        {
            // Ignore if tables already exist
        }

        // Add IsLocked column to UserSessions if it does not yet exist
        try
        {
            if (isPostgres)
            {
                if (!ColumnExists(context, "UserSessions", "IsLocked", isPostgres))
                {
                    context.Database.ExecuteSqlRaw("ALTER TABLE \"UserSessions\" ADD COLUMN \"IsLocked\" boolean NOT NULL DEFAULT FALSE;");
                }
            }
            else
            {
                context.Database.ExecuteSqlRaw("ALTER TABLE [UserSessions] ADD COLUMN [IsLocked] INTEGER NOT NULL DEFAULT 0;");
            }
        }
        catch (Exception)
        {
            // Ignore if column already exists
        }

        // Add CredentialId column to UserSessions if it does not yet exist
        try
        {
            if (isPostgres)
            {
                if (!ColumnExists(context, "UserSessions", "CredentialId", isPostgres))
                {
                    context.Database.ExecuteSqlRaw("ALTER TABLE \"UserSessions\" ADD COLUMN \"CredentialId\" BYTEA;");
                }
            }
            else
            {
                context.Database.ExecuteSqlRaw("ALTER TABLE [UserSessions] ADD COLUMN [CredentialId] BLOB;");
            }
        }
        catch (Exception)
        {
            // Ignore if column already exists
        }

        // Sanitize existing transactions and recurring payments (rounding to 2 decimal places)
        var allTransactions = context.Transactions.ToList();
        var isDbModified = false;
        foreach (var tx in allTransactions)
        {
            var roundedAmount = Math.Round(tx.Amount, 2, MidpointRounding.AwayFromZero);
            if (tx.Amount != roundedAmount)
            {
                tx.Amount = roundedAmount;
                isDbModified = true;
            }
        }
        var allRecurring = context.RecurringPayments.ToList();
        foreach (var rp in allRecurring)
        {
            var roundedAmount = Math.Round(rp.Amount, 2, MidpointRounding.AwayFromZero);
            if (rp.Amount != roundedAmount)
            {
                rp.Amount = roundedAmount;
                isDbModified = true;
            }
        }
        if (isDbModified)
        {
            context.SaveChanges();
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
                new() { Id = "cat-9", Name = "Other" },
                new() { Id = "cat-10", Name = "Transfer" }
            };

            context.TransactionCategories.AddRange(categories);
            context.SaveChanges();
        }
        else if (!context.TransactionCategories.Any(c => c.Name == "Transfer"))
        {
            context.TransactionCategories.Add(new() { Id = "cat-10", Name = "Transfer" });
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
                CycleDay = 28,
                HideSensitive = true,
                VibrationEnabled = true
            };

            context.FinancialSettings.Add(settings);
            context.SaveChanges();
        }
    }

    private static bool ColumnExists(AppDbContext context, string tableName, string columnName, bool isPostgres)
    {
        if (!isPostgres) return false;
        try
        {
            using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = '{tableName}' AND column_name = '{columnName}');";
            if (command.Connection.State != System.Data.ConnectionState.Open)
            {
                command.Connection.Open();
            }
            var result = command.ExecuteScalar();
            return result != null && (bool)result;
        }
        catch
        {
            return false;
        }
    }

    private static bool TableExists(AppDbContext context, string tableName, bool isPostgres)
    {
        if (!isPostgres) return false;
        try
        {
            using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = '{tableName}');";
            if (command.Connection.State != System.Data.ConnectionState.Open)
            {
                command.Connection.Open();
            }
            var result = command.ExecuteScalar();
            return result != null && (bool)result;
        }
        catch
        {
            return false;
        }
    }
}
