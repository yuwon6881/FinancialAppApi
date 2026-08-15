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

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
