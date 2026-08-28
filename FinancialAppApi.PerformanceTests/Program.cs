using System.Diagnostics;
using System.Data.Common;
using System.Text.Json;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

// This executable is deliberately outside FinancialAppApi.sln. The ordinary unit/integration
// suite remains provider-independent and fast; CI invokes this project only in a disposable local
// PostgreSQL service job.
var connectionString = Environment.GetEnvironmentVariable("FINANCIALAPP_PERFORMANCE_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("FINANCIALAPP_PERFORMANCE_CONNECTION is required for the PostgreSQL benchmark.");
    return 2;
}

PerformanceConnectionGuard.Validate(connectionString);
var profile = Environment.GetEnvironmentVariable("FINANCIALAPP_PERFORMANCE_PROFILE")?.Trim().ToLowerInvariant() ?? "both";
var profiles = profile switch
{
    "small" => new[] { PerformanceProfile.Small },
    "long" or "long-history" => new[] { PerformanceProfile.LongHistory },
    "both" => new[] { PerformanceProfile.Small, PerformanceProfile.LongHistory },
    _ => throw new ArgumentException("FINANCIALAPP_PERFORMANCE_PROFILE must be small, long, or both."),
};

var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
    .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure());
await using (var setup = new AppDbContext(optionsBuilder.Options))
{
    await setup.Database.EnsureDeletedAsync();
    await setup.Database.EnsureCreatedAsync();
    foreach (var selectedProfile in profiles)
    {
        await PerformanceSeed.SeedAsync(setup, selectedProfile);
    }
}

var reports = new List<ProfileReport>();
foreach (var selectedProfile in profiles)
{
    var sequential = new List<Sample>();
    for (var iteration = 0; iteration < 3; iteration++)
    {
        sequential.Add(await BenchmarkQueriesAsync(connectionString, selectedProfile));
    }

    var concurrent = await Task.WhenAll(
        Enumerable.Range(0, 10)
            .Select(_ => BenchmarkQueriesAsync(connectionString, selectedProfile)));
    reports.Add(new ProfileReport(
        selectedProfile.ToString(),
        PerformanceSeed.TransactionCount(selectedProfile),
        Summarize(sequential),
        Summarize(concurrent)));
}

if (reports.Count == 2)
{
    var smallCommands = reports[0].Sequential.AverageDatabaseCommands;
    var longCommands = reports[1].Sequential.AverageDatabaseCommands;
    if (smallCommands != longCommands)
        throw new InvalidOperationException($"History-size query regression detected: small={smallCommands}, long={longCommands}.");
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    generatedAtUtc = DateTime.UtcNow,
    databaseHost = new NpgsqlConnectionStringBuilder(connectionString).Host,
    profiles = reports,
}, new JsonSerializerOptions { WriteIndented = true }));
return 0;

static async Task<Sample> BenchmarkQueriesAsync(
    string connectionString,
    PerformanceProfile profile)
{
    var counter = new CountingCommandInterceptor();
    // The production interceptor is registered by DI. This isolated harness uses a fresh context
    // so its command count covers exactly this sample and cannot leak across concurrent runs.
    var measuredOptions = new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(connectionString)
        .AddInterceptors(counter)
        .Options;
    await using var measured = new AppDbContext(measuredOptions);
    measured.SetCurrentUser(PerformanceSeed.UserId(profile));
    var queries = new TransactionQueryService(measured);
    var stopwatch = Stopwatch.StartNew();

    var cycle = await queries.GetTransactionsAsync(
        queryMonth: "Jul",
        queryYear: 2026,
        all: false,
        page: 1,
        pageSize: 50,
        cancellationToken: CancellationToken.None);
    var all = await queries.GetTransactionsAsync(
        all: true,
        page: 1,
        pageSize: 50,
        search: "transaction",
        searchMode: "partial",
        cancellationToken: CancellationToken.None);
    var autocomplete = await queries.GetAutocompleteSuggestionsAsync();
    var recurring = await measured.RecurringPayments.AsNoTracking().ToListAsync();
    var occurrences = await measured.RecurringPaymentOccurrences.AsNoTracking().Take(100).ToListAsync();
    var categories = await measured.TransactionCategories.AsNoTracking().ToListAsync();
    var wishlist = await measured.WishlistItems.AsNoTracking().ToListAsync();
    var goals = await measured.SavingsGoals.AsNoTracking().ToListAsync();
    var loans = await measured.Loans.AsNoTracking().ToListAsync();
    var investments = await measured.InvestmentTransactions.AsNoTracking().Take(100).ToListAsync();
    var marketBars = await measured.MarketPriceBars.AsNoTracking().Take(100).ToListAsync();
    var documents = await measured.VaultDocuments.AsNoTracking().Take(100).ToListAsync();
    stopwatch.Stop();

    var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(new
    {
        cycle = cycle.Items.Count,
        all = all.Items.Count,
        autocomplete = autocomplete.Count,
        recurring = recurring.Count,
        occurrences = occurrences.Count,
        categories = categories.Count,
        wishlist = wishlist.Count,
        goals = goals.Count,
        loans = loans.Count,
        investments = investments.Count,
        marketBars = marketBars.Count,
        documents = documents.Count,
    }).LongLength;
    return new Sample(stopwatch.Elapsed.TotalMilliseconds, counter.Count, payloadBytes,
        Process.GetCurrentProcess().WorkingSet64);
}

static Summary Summarize(IEnumerable<Sample> samples)
{
    var values = samples.OrderBy(sample => sample.LatencyMs).ToArray();
    static double Percentile(Sample[] values, double percentile)
    {
        if (values.Length == 0) return 0;
        var index = Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1);
        return values[index].LatencyMs;
    }

    return new Summary(
        Percentile(values, .50),
        Percentile(values, .95),
        values.Length == 0 ? 0 : values[^1].LatencyMs,
        values.Length == 0 ? 0 : values.Average(sample => sample.DatabaseCommands),
        values.Length == 0 ? 0 : values.Average(sample => sample.PayloadBytes),
        values.Length == 0 ? 0 : values.Max(sample => sample.WorkingSetBytes));
}

public enum PerformanceProfile { Small, LongHistory }
public sealed record Sample(double LatencyMs, int DatabaseCommands, long PayloadBytes, long WorkingSetBytes);
public sealed record Summary(double P50Ms, double P95Ms, double MaxMs, double AverageDatabaseCommands, double AveragePayloadBytes, long MaxWorkingSetBytes);
public sealed record ProfileReport(string Profile, int Transactions, Summary Sequential, Summary Concurrent);

public static class PerformanceConnectionGuard
{
    public static void Validate(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var host = (builder.Host ?? string.Empty).Trim().ToLowerInvariant();
        if (host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new InvalidOperationException("Performance tests accept only a loopback PostgreSQL host.");
        if (!(builder.Database ?? string.Empty).StartsWith("financialapp_perf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Performance tests require a disposable financialapp_perf database.");
    }
}

public sealed class CountingCommandInterceptor : DbCommandInterceptor
{
    private int _count;
    public int Count => Volatile.Read(ref _count);
    private void Increment() => Interlocked.Increment(ref _count);
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result) { Increment(); return result; }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default) { Increment(); return ValueTask.FromResult(result); }
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result) { Increment(); return result; }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default) { Increment(); return ValueTask.FromResult(result); }
    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result) { Increment(); return result; }
    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default) { Increment(); return ValueTask.FromResult(result); }
}

public static class PerformanceSeed
{
    public static int TransactionCount(PerformanceProfile profile) => profile == PerformanceProfile.Small ? 1_000 : 120_000;
    public static string UserId(PerformanceProfile profile) => profile == PerformanceProfile.Small ? "perf-small" : "perf-long";

public static async Task SeedAsync(AppDbContext context, PerformanceProfile profile)
    {
        var userId = UserId(profile);
        context.SetCurrentUser(userId);
        var transactionDate = UtcDate(2026, 7, 10);
        context.AppUsers.Add(new AppUser
        {
            Id = userId,
            Username = userId,
            PasswordHash = "performance-only",
        });
        context.FinancialSettings.Add(new FinancialSetting
        {
            UserId = userId,
            TargetStabilityFund = 10_000,
            SelectedMonth = "Jul",
            SelectedYear = 2026,
            EssentialsAlloc = .5m,
            GrowthAlloc = .25m,
            StabilityAlloc = .15m,
            RewardsAlloc = .1m,
            CycleDay = 28,
            HideSensitive = false,
            Currency = "USD",
            CategoryLimitAlertsEnabled = false,
        });
        var accounts = new[] { "Essentials", "Growth", "Stability", "Rewards" }
            .Select(bucket => new LedgerAccount
            {
                Id = $"{userId}-{bucket.ToLowerInvariant()}",
                UserId = userId,
                Name = $"{bucket} account",
                Bucket = bucket,
                Kind = LedgerAccountKind.Bank,
            }).ToList();
        context.LedgerAccounts.AddRange(accounts);
        context.TransactionCategories.AddRange(new[] { "Salary", "Food", "Transport", "Transfer", "Other" }
            .Select((name, index) => new TransactionCategory
            {
                Id = $"{userId}-category-{index}", UserId = userId, Name = name,
                Type = name == "Salary" ? CategoryFlowType.Inflow : CategoryFlowType.Both,
            }));

        var recurringId = $"{userId}-recurring";
        context.RecurringPayments.Add(new RecurringPayment
        {
            Id = recurringId, UserId = userId, Name = "Performance subscription", Amount = 35,
            Frequency = "Monthly", Category = "Software", LedgerCategory = "Essentials",
            AccountId = accounts[0].Id, DueDate = 15, StartDate = "2016-01-01", Active = true,
            OccurrenceTrackingStartDate = new DateOnly(2016, 1, 1), PaymentMode = RecurringPaymentMode.Manual,
        });
        context.RecurringPaymentOccurrences.AddRange(Enumerable.Range(0, 24).Select(index => new RecurringPaymentOccurrence
        {
            Id = $"{recurringId}-occ-{index}", UserId = userId, RecurringPaymentId = recurringId,
            OccurrenceDate = new DateOnly(2026, 1, 1).AddMonths(index), Name = "Performance subscription",
            ScheduledAmount = 35, Category = "Software", LedgerCategory = "Essentials",
            AccountId = accounts[0].Id, PaymentMode = RecurringPaymentMode.Manual,
            Status = RecurringOccurrenceStatus.Pending,
        }));
        context.WishlistItems.Add(new WishlistItem
        {
            UserId = userId, Name = "Performance goal", Price = 500, Priority = "Medium",
            IsPurchased = false, IsActive = true,
        });
        context.SavingsGoals.Add(new SavingsGoal
        {
            UserId = userId, Name = "Performance reserve", TargetAmount = 1000,
            EarmarkedAmount = 100, FundingBucket = SavingsGoalFundingBucket.Rewards,
            TargetDate = new DateTime(2027, 1, 1), Priority = "Medium", Status = SavingsGoalStatus.Active,
            IsRecurring = false, RecurrenceMonths = 12, CycleFundedAmount = 0,
        });
        context.Loans.Add(new Loan
        {
            Id = $"{userId}-loan", UserId = userId, Name = "Performance loan", RecurringPaymentId = recurringId,
            OpeningPrincipal = 5000, TrackingStartDate = new DateOnly(2016, 1, 1), AnnualRatePercent = 4,
            RateBasis = LoanRateBasis.Yearly, TermPeriods = 60, InterestMethod = LoanInterestMethod.ReducingBalance,
            ScheduleFrequency = "Monthly", ScheduleDueDay = 15, ScheduleStartDate = new DateOnly(2016, 1, 1),
            ScheduleStatus = LoanScheduleStatus.Complete,
        });

        // Keep one representative row for each expensive/structural read model in both profiles.
        // These are deliberately synthetic and local-only; the benchmark never calls a provider
        // or writes to a production database.
        var accountMove = new Transaction
        {
            Id = $"{userId}-account-move",
            UserId = userId,
            Date = transactionDate,
            PostedAt = transactionDate.AddHours(8),
            Description = "Performance account move",
            Category = "Transfer",
            LedgerCategory = "AccountMove",
            Amount = 25,
            AccountId = accounts[0].Id,
            CounterAccountId = accounts[1].Id,
            StabilityReloadIntent = StabilityReloadIntent.Unanswered,
        };
        var stabilityDrawdown = new Transaction
        {
            Id = $"{userId}-stability-drawdown",
            UserId = userId,
            Date = transactionDate,
            PostedAt = transactionDate.AddHours(9),
            Description = "Performance stability drawdown",
            Category = "Emergency",
            LedgerCategory = "Stability",
            Amount = -75,
            AccountId = accounts[2].Id,
            StabilityReloadIntent = StabilityReloadIntent.Required,
        };
        context.Transactions.AddRange(accountMove, stabilityDrawdown);
        context.StabilityPlanRevisions.Add(new StabilityPlanRevision
        {
            UserId = userId,
            EffectiveAt = UtcDate(2016, 1, 1),
            TargetStabilityFund = 10_000,
            StabilityAlloc = .15m,
        });

        var investmentAccount = new InvestmentAccount
        {
            UserId = userId,
            Name = "Performance brokerage",
            BaseCurrency = "USD",
        };
        var instrument = new InvestmentInstrument
        {
            UserId = userId,
            Symbol = "PERF",
            Name = "Performance ETF",
            Type = "ETF",
            Currency = "USD",
            ProviderSymbol = "PERF",
            AllocationSleeve = "USEquity",
        };
        context.InvestmentAccounts.Add(investmentAccount);
        context.InvestmentInstruments.Add(instrument);
        context.InvestmentPlans.Add(new InvestmentPlan { UserId = userId });
        context.InvestmentTransactions.Add(new InvestmentTransaction
        {
            UserId = userId,
            Account = investmentAccount,
            Instrument = instrument,
            Type = "Buy",
            TradeDate = new DateOnly(2026, 7, 10),
            Units = 2,
            UnitPrice = 100,
            CashAmount = -200,
        });
        context.InvestmentCashFlows.Add(new InvestmentCashFlow
        {
            UserId = userId,
            Account = investmentAccount,
            Currency = "USD",
            Type = "Deposit",
            Amount = 500,
            Date = new DateOnly(2026, 7, 1),
        });
        context.MarketPriceBars.Add(new MarketPriceBar
        {
            Provider = "performance",
            ExternalInstrumentId = "PERF",
            Symbol = "PERF",
            Mic = "XNAS",
            MarketDate = new DateOnly(2026, 7, 10),
            Close = 100,
        });
        context.FxRateBars.Add(new FxRateBar
        {
            Provider = "performance",
            BaseCurrency = "USD",
            QuoteCurrency = "MYR",
            MarketDate = new DateOnly(2026, 7, 10),
            Rate = 4.7m,
        });
        context.TaxReliefCategoryLimits.Add(new TaxReliefCategoryLimit
        {
            UserId = userId,
            CategoryId = "medical",
            Name = "Medical",
            Limit = 5_000,
            TaxYear = 2026,
        });
        context.VaultDocuments.Add(new VaultDocument
        {
            UserId = userId,
            StorageObjectPath = $"performance/{userId}/receipt.pdf",
            OriginalFileName = "receipt.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            Sha256 = new string('a', 64),
            TaxYear = 2026,
            ReliefCategory = "medical",
            Amount = 100,
            AmountCurrency = "MYR",
            AmountStatus = "Confirmed",
            TransactionId = stabilityDrawdown.Id,
            UploadedAt = transactionDate,
            RetentionUntil = new DateOnly(2036, 7, 10),
            ClientKey = $"{userId}-document",
        });

        var count = TransactionCount(profile);
        var start = profile == PerformanceProfile.Small ? UtcDate(2025, 1, 1) : UtcDate(2016, 1, 1);
        var rows = new List<Transaction>(Math.Min(count, 2_000));
        for (var index = 0; index < count; index++)
        {
            var isIncome = index % 10 == 0;
            var isRecurring = index % 500 == 1;
            rows.Add(new Transaction
            {
                Id = $"{userId}-tx-{index:D6}", UserId = userId,
                Date = start.AddDays(index % (profile == PerformanceProfile.Small ? 365 : 3650)),
                PostedAt = start.AddDays(index % (profile == PerformanceProfile.Small ? 365 : 3650)).AddHours(12),
                Description = $"Performance transaction {index:D6}",
                Category = isIncome ? "Salary" : "Food", LedgerCategory = isIncome ? "Income" : "Essentials",
                Amount = isIncome ? 2500 : -25, AccountId = isIncome ? null : accounts[index % accounts.Count].Id,
                StabilityReloadIntent = StabilityReloadIntent.Unanswered,
                RecurringPaymentId = isRecurring ? recurringId : null,
                RecurringOccurrenceDate = isRecurring ? new DateOnly(2026, 1, 1).AddMonths(index / 500) : null,
            });
            if (rows.Count < 2_000 && index + 1 < count) continue;
            context.Transactions.AddRange(rows);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            rows.Clear();
        }
        await context.SaveChangesAsync();
    }

    private static DateTime UtcDate(int year, int month, int day) =>
        new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
}
