using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Accounts;

namespace FinancialAppApi.Tests;

public sealed class LedgerAccountInterestServiceTests
{
    [Fact]
    public async Task AccountMutationsPersistInterestSettingsAndScheduleTheNextPosting()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var service = new LedgerAccountService(
            context,
            new LedgerAccountBalanceService(context),
            new CycleBalanceService(context),
            FinancialClock.Utc);

        var result = await service.CreateAsync(new LedgerAccountMutation(
            "acct-configured-interest",
            "Configured interest account",
            "Essentials",
            LedgerAccountKind.Bank,
            false,
            InterestEnabled: true,
            InterestRatePercent: 5m,
            InterestFrequency: LedgerAccountInterestFrequency.Monthly));

        Assert.Equal(LedgerAccountMutationStatus.Success, result.Status);
        Assert.True(result.Account!.InterestEnabled);
        Assert.Equal(5m, result.Account.InterestRatePercent);
        Assert.Equal(LedgerAccountInterestFrequency.Monthly, result.Account.InterestFrequency);
        Assert.Equal(FinancialClock.Utc.Today.Day, result.Account.InterestAnchorDay);
        Assert.Equal(FinancialClock.Utc.Today.AddMonths(1), result.Account.InterestNextAccrualDate);

        var update = await service.UpdateAsync(
            result.Account.Id,
            new LedgerAccountMutation(
                result.Account.Id,
                result.Account.Name,
                result.Account.Bucket,
                result.Account.Kind,
                false));

        Assert.Equal(LedgerAccountMutationStatus.Success, update.Status);
        Assert.True(update.Account!.InterestEnabled);
        Assert.Equal(5m, update.Account.InterestRatePercent);
        Assert.Equal(LedgerAccountInterestFrequency.Monthly, update.Account.InterestFrequency);
    }

    [Fact]
    public void CalculateAccrualUsesTheAnnualRateAndSelectedPostingFrequency()
    {
        var monthly = LedgerAccountInterestService.CalculateAccrual(
            1_000m,
            12m,
            LedgerAccountInterestFrequency.Monthly,
            0m);
        var daily = LedgerAccountInterestService.CalculateAccrual(
            1_000m,
            12m,
            LedgerAccountInterestFrequency.Daily,
            0m);
        var yearly = LedgerAccountInterestService.CalculateAccrual(
            1_000m,
            12m,
            LedgerAccountInterestFrequency.Yearly,
            0m);

        Assert.Equal(10m, monthly.PostedAmount);
        Assert.Equal(0.32m, daily.PostedAmount);
        Assert.Equal(120m, yearly.PostedAmount);
        Assert.True(daily.Remainder > 0m);
    }

    [Fact]
    public void NextDatePreservesTheOriginalCalendarDayAcrossClampedMonths()
    {
        Assert.Equal(
            new DateOnly(2026, 2, 28),
            LedgerAccountInterestFrequency.NextDate(new DateOnly(2026, 1, 31), LedgerAccountInterestFrequency.Monthly, 31));
        Assert.Equal(
            new DateOnly(2026, 3, 31),
            LedgerAccountInterestFrequency.NextDate(new DateOnly(2026, 2, 28), LedgerAccountInterestFrequency.Monthly, 31));
        Assert.Equal(
            new DateOnly(2028, 2, 29),
            LedgerAccountInterestFrequency.NextDate(new DateOnly(2028, 1, 31), LedgerAccountInterestFrequency.Monthly, 31));
        Assert.Equal(
            new DateOnly(2025, 2, 28),
            LedgerAccountInterestFrequency.NextDate(new DateOnly(2024, 2, 29), LedgerAccountInterestFrequency.Yearly, 29));
        Assert.Equal(
            new DateOnly(2026, 2, 28),
            LedgerAccountInterestFrequency.NextDate(new DateOnly(2025, 2, 28), LedgerAccountInterestFrequency.Yearly, 29));
    }

    [Fact]
    public async Task EditingInterestTermsPreservesTheFutureScheduleAndRemainder()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var today = FinancialClock.Utc.Today;
        var nextDate = today.AddDays(10);
        var account = new LedgerAccount
        {
            Id = "acct-interest-edit",
            Name = "Interest edit",
            UserId = TestHelpers.DefaultUserId,
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank,
            InterestEnabled = true,
            InterestRatePercent = 4m,
            InterestFrequency = LedgerAccountInterestFrequency.Monthly,
            InterestAnchorDay = 31,
            InterestNextAccrualDate = nextDate,
            InterestRemainder = 0.004m,
        };
        context.LedgerAccounts.Add(account);
        await context.SaveChangesAsync();

        var service = new LedgerAccountService(
            context,
            new LedgerAccountBalanceService(context),
            new CycleBalanceService(context),
            FinancialClock.Utc);
        var result = await service.UpdateAsync(account.Id, new LedgerAccountMutation(
            account.Id,
            account.Name,
            account.Bucket,
            account.Kind,
            false,
            InterestEnabled: true,
            InterestRatePercent: 4.25m,
            InterestFrequency: LedgerAccountInterestFrequency.Yearly));

        Assert.Equal(LedgerAccountMutationStatus.Success, result.Status);
        Assert.Equal(nextDate, result.Account!.InterestNextAccrualDate);
        Assert.Equal(31, result.Account.InterestAnchorDay);
        Assert.Equal(0.004m, result.Account.InterestRemainder);
    }

    [Fact]
    public async Task ApplyDueInterestPostsCatchUpRowsAndDoesNotDuplicateThem()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var today = FinancialClock.Utc.Today;
        var account = new LedgerAccount
        {
            Id = "acct-interest",
            Name = "Interest account",
            UserId = TestHelpers.DefaultUserId,
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank,
            InterestEnabled = true,
            InterestRatePercent = 12m,
            InterestFrequency = LedgerAccountInterestFrequency.Daily,
            InterestNextAccrualDate = today.AddDays(-1),
        };
        context.LedgerAccounts.Add(account);
        context.Transactions.Add(new Transaction
        {
            Id = "acct-interest-opening",
            Date = TransactionDate.StartOfDate(today.AddDays(-3)),
            Description = "Opening balance",
            Category = "Adjustment",
            LedgerCategory = "Essentials",
            Amount = 1_000m,
            AccountId = account.Id,
        });
        await context.SaveChangesAsync();

        var service = new LedgerAccountInterestService(
            context,
            new LedgerAccountBalanceService(context),
            FinancialClock.Utc);

        var first = await service.ApplyDueInterestAsync();
        var rows = context.Transactions
            .Where(transaction => transaction.Id.StartsWith("acct-interest-interest-"))
            .OrderBy(transaction => transaction.Date)
            .ToList();

        Assert.True(first.HasChanges);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { 0.32m, 0.33m }, rows.Select(row => row.Amount).ToArray());
        Assert.Equal(today.AddDays(1), account.InterestNextAccrualDate);
        Assert.True(account.InterestRemainder > 0m);

        var second = await service.ApplyDueInterestAsync();

        Assert.False(second.HasChanges);
        Assert.Equal(2, context.Transactions.Count(transaction => transaction.Id.StartsWith("acct-interest-interest-")));
    }

    [Fact]
    public async Task CatchUpAcrossManyMissedPeriods_IsDeterministicAndIdempotent()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var today = new DateOnly(2026, 8, 15);
        var clock = new FinancialClock(
            TestHelpers.NewConfiguration(("Financial:TimeZoneId", "UTC")),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero)));

        var account = new LedgerAccount
        {
            Id = "acct-missed-periods",
            Name = "Savings with Missed Interest",
            UserId = TestHelpers.DefaultUserId,
            Bucket = "Stability",
            Kind = LedgerAccountKind.Bank,
            InterestEnabled = true,
            InterestRatePercent = 12m,
            InterestFrequency = LedgerAccountInterestFrequency.Monthly,
            InterestNextAccrualDate = new DateOnly(2025, 8, 15), // 12 missed monthly periods
        };
        context.LedgerAccounts.Add(account);
        context.Transactions.Add(new Transaction
        {
            Id = "tx-opening",
            Date = TransactionDate.StartOfDate(new DateOnly(2025, 7, 1)),
            PostedAt = DateTime.UtcNow,
            Description = "Deposit",
            Category = "Adjustment",
            LedgerCategory = "Stability",
            Amount = 10_000m,
            AccountId = account.Id,
        });
        await context.SaveChangesAsync();

        var service = new LedgerAccountInterestService(
            context,
            new LedgerAccountBalanceService(context),
            clock);

        var result = await service.ApplyDueInterestAsync();
        Assert.True(result.HasChanges);
        Assert.Equal(new DateOnly(2025, 8, 15), result.EarliestPostedDate);

        var interestRows = context.Transactions
            .Where(t => t.AccountId == account.Id && t.Category == "Interest")
            .OrderBy(t => t.Date)
            .ToList();

        // 13 monthly postings (from 2025-08-15 through 2026-08-15)
        Assert.Equal(13, interestRows.Count);
        Assert.Equal(new DateOnly(2026, 9, 15), account.InterestNextAccrualDate);

        // Repeated run does nothing and leaves the exact same 13 rows
        var repeated = await service.ApplyDueInterestAsync();
        Assert.False(repeated.HasChanges);
        Assert.Equal(13, context.Transactions.Count(t => t.AccountId == account.Id && t.Category == "Interest"));
    }

    [Fact]
    public void SubCentRemainderAccumulatesAcrossPeriods()
    {
        // $100 at 3.65% annual rate daily
        // Daily rate = 100 * 0.0365 / 365 = 0.01 (exact 1 cent per day)
        // With $50: daily = 50 * 0.0365 / 365 = 0.005 (0.5 cent per day)
        var day1 = LedgerAccountInterestService.CalculateAccrual(50m, 3.65m, LedgerAccountInterestFrequency.Daily, 0m);
        Assert.Equal(0.00m, day1.PostedAmount);
        Assert.Equal(0.005m, day1.Remainder);

        var day2 = LedgerAccountInterestService.CalculateAccrual(50m, 3.65m, LedgerAccountInterestFrequency.Daily, day1.Remainder);
        Assert.Equal(0.01m, day2.PostedAmount); // 0.005 + 0.005 = 0.01 posted
        Assert.Equal(0.00m, day2.Remainder);
    }

    [Fact]
    public void SubCentRemainderSurvivesAPeriodThatEarnsNothing()
    {
        // A day at zero balance must not destroy what earlier days banked: the carry belongs to
        // the account, and wiping it meant a low-balance account could earn fractions for weeks
        // and post nothing the first time the balance touched zero.
        var banked = LedgerAccountInterestService.CalculateAccrual(
            50m, 3.65m, LedgerAccountInterestFrequency.Daily, 0m);
        Assert.Equal(0.005m, banked.Remainder);

        var emptied = LedgerAccountInterestService.CalculateAccrual(
            0m, 3.65m, LedgerAccountInterestFrequency.Daily, banked.Remainder);
        Assert.Equal(0.00m, emptied.PostedAmount);
        Assert.Equal(0.005m, emptied.Remainder);

        var unrated = LedgerAccountInterestService.CalculateAccrual(
            50m, 0m, LedgerAccountInterestFrequency.Daily, banked.Remainder);
        Assert.Equal(0.005m, unrated.Remainder);

        // ...and the carry still completes a cent once earning resumes.
        var resumed = LedgerAccountInterestService.CalculateAccrual(
            50m, 3.65m, LedgerAccountInterestFrequency.Daily, emptied.Remainder);
        Assert.Equal(0.01m, resumed.PostedAmount);
    }

    [Fact]
    public async Task PostingInterestProvisionsItsCategoryExactlyOnce()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var today = FinancialClock.Utc.Today;
        var account = new LedgerAccount
        {
            Id = "acct-category",
            Name = "Interest account",
            UserId = TestHelpers.DefaultUserId,
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank,
            InterestEnabled = true,
            InterestRatePercent = 12m,
            InterestFrequency = LedgerAccountInterestFrequency.Daily,
            InterestNextAccrualDate = today.AddDays(-1),
        };
        context.LedgerAccounts.Add(account);
        context.Transactions.Add(new Transaction
        {
            Id = "acct-category-opening",
            Date = TransactionDate.StartOfDate(today.AddDays(-3)),
            Description = "Opening balance",
            Category = "Adjustment",
            LedgerCategory = "Essentials",
            Amount = 1_000m,
            AccountId = account.Id,
        });
        await context.SaveChangesAsync();

        Assert.Empty(context.TransactionCategories.Where(
            category => category.Name == LedgerAccountInterestService.CategoryName));

        var service = new LedgerAccountInterestService(
            context,
            new LedgerAccountBalanceService(context),
            FinancialClock.Utc);
        await service.ApplyDueInterestAsync();

        // Interest rows are filed under a category the user must actually have, or they surface in
        // reports under a name that is missing from Settings, from the category filters and from
        // /api/categories/usage.
        var categories = context.TransactionCategories
            .Where(category => category.Name == LedgerAccountInterestService.CategoryName)
            .ToList();
        var provisioned = Assert.Single(categories);
        Assert.Equal(CategoryFlowType.Inflow, provisioned.Type);
        Assert.Equal(TestHelpers.DefaultUserId, provisioned.UserId);
        Assert.All(
            context.Transactions.Where(t => t.AccountId == account.Id && t.Id.Contains("-interest-")),
            row => Assert.Equal(LedgerAccountInterestService.CategoryName, row.Category));

        // A second account posting through a fresh service instance must not add a duplicate: the
        // once-per-scope guard is an optimisation, and the existence query is the real check.
        context.LedgerAccounts.Add(new LedgerAccount
        {
            Id = "acct-category-second",
            Name = "Second interest account",
            UserId = TestHelpers.DefaultUserId,
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank,
            InterestEnabled = true,
            InterestRatePercent = 12m,
            InterestFrequency = LedgerAccountInterestFrequency.Daily,
            InterestNextAccrualDate = today,
        });
        context.Transactions.Add(new Transaction
        {
            Id = "acct-category-second-opening",
            Date = TransactionDate.StartOfDate(today.AddDays(-3)),
            Description = "Opening balance",
            Category = "Adjustment",
            LedgerCategory = "Essentials",
            Amount = 1_000m,
            AccountId = "acct-category-second",
        });
        await context.SaveChangesAsync();

        var later = new LedgerAccountInterestService(
            context,
            new LedgerAccountBalanceService(context),
            FinancialClock.Utc);
        await later.ApplyDueInterestAsync();
        Assert.Contains(
            context.Transactions,
            row => row.Id.StartsWith("acct-category-second-interest-"));

        Assert.Single(context.TransactionCategories.Where(
            category => category.Name == LedgerAccountInterestService.CategoryName));
    }

    [Fact]
    public async Task PostingInterestUsesTheExistingCategorySpelling()
    {
        await using var context = TestHelpers.NewInMemoryContext();
        var today = FinancialClock.Utc.Today;
        context.TransactionCategories.Add(new TransactionCategory
        {
            Id = "cat-existing-interest",
            Name = "interest",
            Type = CategoryFlowType.Inflow,
            UserId = TestHelpers.DefaultUserId,
        });
        var account = new LedgerAccount
        {
            Id = "acct-existing-category",
            Name = "Existing category",
            UserId = TestHelpers.DefaultUserId,
            Bucket = "Essentials",
            Kind = LedgerAccountKind.Bank,
            InterestEnabled = true,
            InterestRatePercent = 12m,
            InterestFrequency = LedgerAccountInterestFrequency.Daily,
            InterestNextAccrualDate = today,
        };
        context.LedgerAccounts.Add(account);
        context.Transactions.Add(new Transaction
        {
            Id = "acct-existing-category-opening",
            Date = TransactionDate.StartOfDate(today.AddDays(-1)),
            PostedAt = DateTime.UtcNow,
            Description = "Opening balance",
            Category = "Adjustment",
            LedgerCategory = "Essentials",
            Amount = 1_000m,
            AccountId = account.Id,
        });
        await context.SaveChangesAsync();

        var service = new LedgerAccountInterestService(
            context,
            new LedgerAccountBalanceService(context),
            FinancialClock.Utc);
        await service.ApplyDueInterestAsync();

        var row = Assert.Single(context.Transactions.Where(
            transaction => transaction.Id.StartsWith("acct-existing-category-interest-")));
        Assert.Equal("interest", row.Category);
        Assert.Single(context.TransactionCategories.Where(category => category.Name.ToLower() == "interest"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
