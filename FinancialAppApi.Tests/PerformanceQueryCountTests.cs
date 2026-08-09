using System.Data.Common;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Investments;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialAppApi.Tests;

public sealed class PerformanceQueryCountTests
{
    // Settings, active payments, cycle-relevant transactions, and the recurring-occurrence
    // ledger -- four reads, each of a distinct shared table, and no repeats. The ledger's
    // backfill deliberately reuses the transactions already loaded here rather than scanning
    // them a second time; that reuse is what keeps this at four rather than five.
    [Fact]
    public async Task BootstrapSnapshot_LoadsSharedCoreDataInFourQueries()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        fixture.Context.FinancialSettings.Add(new FinancialSetting
        {
            UserId = TestHelpers.DefaultUserId,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            CycleDay = 1
        });
        await fixture.Context.SaveChangesAsync();
        fixture.Counter.Reset();

        var occurrenceService = new RecurringOccurrenceService(
            NullLogger<RecurringOccurrenceService>.Instance);
        var financialService = new FinancialService(
            fixture.Context,
            new CycleBalanceService(fixture.Context),
            new RecurringPaymentAlertService(
                fixture.Context,
                occurrenceService),
            occurrenceService);

        _ = await financialService.CreateBootstrapSnapshotAsync(
            "Jul",
            2026,
            persistSelection: false,
            CancellationToken.None);

        Assert.Equal(4, fixture.Counter.CommandCount);
    }

    // The full dashboard reuses the snapshot's settings and active recurring payments when it
    // builds the pending-bill alerts. The alert service used to re-read both itself, which cost
    // /api/bootstrap two extra round trips on every cold start and every post-drain refresh.
    [Fact]
    public async Task DashboardData_ReusesSnapshotDataForPendingBillAlerts()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        fixture.Context.FinancialSettings.Add(new FinancialSetting
        {
            UserId = TestHelpers.DefaultUserId,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            CycleDay = 1
        });
        fixture.Context.RecurringPayments.Add(new RecurringPayment
        {
            Id = "rp-1",
            UserId = TestHelpers.DefaultUserId,
            Name = "Streaming",
            Amount = 15m,
            Category = "Entertainment",
            LedgerCategory = "Rewards",
            Frequency = "Monthly",
            StartDate = "2026-01-05",
            PaymentMode = RecurringPaymentMode.Manual,
            Active = true
        });
        await fixture.Context.SaveChangesAsync();

        var occurrenceService = new RecurringOccurrenceService(
            NullLogger<RecurringOccurrenceService>.Instance);
        var financialService = new FinancialService(
            fixture.Context,
            new CycleBalanceService(fixture.Context),
            new RecurringPaymentAlertService(fixture.Context, occurrenceService),
            occurrenceService);

        var snapshot = await financialService.CreateBootstrapSnapshotAsync(
            "Jul",
            2026,
            persistSelection: false,
            CancellationToken.None);
        // Warm-up. The very first read of a bill also *materialises* its occurrence rows back
        // to the tracking start, which is a one-time write, not a per-request cost. The guard
        // below is about the steady-state round trips every later request pays.
        await new RecurringPaymentAlertService(fixture.Context, occurrenceService)
            .GetSubscriptionAlertsAsync(CancellationToken.None);

        // Standalone: settings + active payments + occurrence ledger.
        fixture.Counter.Reset();
        var standaloneAlerts = await new RecurringPaymentAlertService(fixture.Context, occurrenceService)
            .GetSubscriptionAlertsAsync(CancellationToken.None);
        Assert.Equal(3, fixture.Counter.CommandCount);

        // Dashboard path: settings and payments come from the snapshot, so only the ledger is
        // read. One query covers the whole range -- materialising a range must never cost a
        // lookup per occurrence date.
        fixture.Counter.Reset();
        var snapshotAlerts = await new RecurringPaymentAlertService(fixture.Context, occurrenceService)
            .GetSubscriptionAlertsAsync(
                snapshot.Cycle.CycleDay,
                snapshot.ActiveRecurringPayments,
                CancellationToken.None);
        Assert.Equal(1, fixture.Counter.CommandCount);

        // Fewer queries, same answer -- the point of the overload is the round trips, not a
        // different result.
        Assert.Equal(standaloneAlerts.Count, snapshotAlerts.Count);
        Assert.NotEmpty(snapshotAlerts);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2_000)]
    public async Task InvestmentTransactionValidation_ReusesThreeQuerySnapshotAtAnyHistorySize(
        int historySize)
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var accountId = Guid.NewGuid();
        fixture.Context.InvestmentAccounts.Add(new InvestmentAccount
        {
            Id = accountId,
            UserId = TestHelpers.DefaultUserId,
            Name = "Query-count account",
            BaseCurrency = "USD"
        });
        for (var index = 0; index < historySize; index++)
        {
            fixture.Context.InvestmentCashFlows.Add(new InvestmentCashFlow
            {
                Id = Guid.NewGuid(),
                UserId = TestHelpers.DefaultUserId,
                AccountId = accountId,
                Type = "Deposit",
                Date = new DateOnly(2020, 1, 1).AddDays(index),
                Currency = "USD",
                Amount = 100m,
                CreatedAt = DateTime.UtcNow.AddSeconds(index),
                UpdatedAt = DateTime.UtcNow.AddSeconds(index)
            });
        }
        await fixture.Context.SaveChangesAsync();

        fixture.Context.InvestmentCashFlows.Add(new InvestmentCashFlow
        {
            Id = Guid.NewGuid(),
            UserId = TestHelpers.DefaultUserId,
            AccountId = accountId,
            Type = "Withdrawal",
            Date = new DateOnly(2026, 7, 2),
            Currency = "USD",
            Amount = -10m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        fixture.Counter.Reset();

        var service = new InvestmentHistoryValidationService(
            fixture.Context,
            new InvestmentAccountingService());

        var result = await service.ValidateTransactionMutationAsync(CancellationToken.None);

        Assert.Null(result.PositionError);
        Assert.Null(result.CashError);
        Assert.Equal(3, fixture.Counter.CommandCount);
    }

    private sealed class SqliteFixture : IAsyncDisposable
    {
        private SqliteFixture(
            SqliteConnection connection,
            AppDbContext context,
            CountingCommandInterceptor counter)
        {
            Connection = connection;
            Context = context;
            Counter = counter;
        }

        public SqliteConnection Connection { get; }
        public AppDbContext Context { get; }
        public CountingCommandInterceptor Counter { get; }

        public static async Task<SqliteFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var counter = new CountingCommandInterceptor();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(counter)
                .Options;
            var context = new AppDbContext(options);
            context.SetCurrentUser(TestHelpers.DefaultUserId);
            await context.Database.EnsureCreatedAsync();
            context.AppUsers.Add(new AppUser
            {
                Id = TestHelpers.DefaultUserId,
                Username = "query-count-user",
                NormalizedUsername = "QUERY-COUNT-USER",
                PasswordHash = "not-used"
            });
            await context.SaveChangesAsync();
            counter.Reset();
            return new SqliteFixture(connection, context, counter);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        public int CommandCount { get; private set; }

        public void Reset() => CommandCount = 0;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            CommandCount++;
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return ValueTask.FromResult(result);
        }
    }
}
