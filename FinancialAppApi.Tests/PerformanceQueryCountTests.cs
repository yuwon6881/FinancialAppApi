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
    [Fact]
    public async Task BootstrapSnapshot_LoadsSharedCoreDataInThreeQueries()
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

        Assert.Equal(3, fixture.Counter.CommandCount);
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
