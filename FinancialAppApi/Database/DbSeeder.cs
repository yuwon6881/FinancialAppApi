using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace FinancialAppApi.Database;

public static class DbSeeder
{
    private static readonly string[] DefaultCategoryNames =
    [
        "Salary", "Social", "Food", "Hobbies", "Software", "Investment",
        "Entertainment", "Transport", "Other", "Transfer"
    ];
    private static readonly string[] LedgerBuckets = ["Essentials", "Growth", "Stability", "Rewards"];

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
                HideSensitive = false,
                VibrationEnabled = true
            });
        }

        // Bootstrap the default categories only for a user who has none yet (i.e. a brand-new
        // account). We deliberately do NOT top up individual missing names: a re-seed on every
        // deployment would otherwise resurrect defaults the user intentionally deleted, since a
        // hard-deleted category is indistinguishable from one that was never created. Seeding
        // is a one-time provisioning step, mirroring the FinancialSettings block above.
        var hasCategories = context.TransactionCategories
            .IgnoreQueryFilters()
            .Any(category => category.UserId == userId);
        if (!hasCategories)
        {
            for (var index = 0; index < DefaultCategoryNames.Length; index++)
            {
                context.TransactionCategories.Add(new TransactionCategory
                {
                    Id = $"cat-{userId}-{index + 1}",
                    UserId = userId,
                    Name = DefaultCategoryNames[index]
                });
            }
        }

        var hasLedgerAccounts = context.LedgerAccounts
            .IgnoreQueryFilters()
            .Any(account => account.UserId == userId);
        if (!hasLedgerAccounts)
        {
            foreach (var bucket in LedgerBuckets)
            {
                context.LedgerAccounts.Add(new LedgerAccount
                {
                    Id = CreateDefaultAccountId(userId, bucket),
                    UserId = userId,
                    Name = $"{bucket} balance",
                    Bucket = bucket,
                    Kind = LedgerAccountKind.Other,
                    IsDefault = true,
                    IsArchived = false,
                });
            }
        }

    }

    private static string CreateDefaultAccountId(string userId, string bucket)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{userId}:{bucket}"));
        return $"acct-default-{Convert.ToHexString(hash)[..32].ToLowerInvariant()}";
    }
}
