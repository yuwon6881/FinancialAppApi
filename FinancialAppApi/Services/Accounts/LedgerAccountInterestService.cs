using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Accounts;

public sealed record LedgerAccountInterestAccrualResult(DateOnly? EarliestPostedDate)
{
    public bool HasChanges => EarliestPostedDate.HasValue;
}

/// <summary>
/// Posts account interest into the account's existing bucket ledger. Interest is therefore part
/// of the same balance source as ordinary activity instead of making account rows drift away from
/// their bucket totals. Catch-up is safe because the account stores the next period date and each
/// generated transaction has a deterministic id.
/// </summary>
public sealed class LedgerAccountInterestService
{
    private readonly AppDbContext _context;
    private readonly LedgerAccountBalanceService _balanceService;
    private readonly FinancialClock _clock;

    public LedgerAccountInterestService(
        AppDbContext context,
        LedgerAccountBalanceService balanceService,
        FinancialClock? clock = null)
    {
        _context = context;
        _balanceService = balanceService;
        _clock = clock ?? FinancialClock.Utc;
    }

    public async Task<LedgerAccountInterestAccrualResult> ApplyDueInterestAsync(
        CancellationToken cancellationToken = default)
    {
        return await ApplyDueInterestAsync(null, cancellationToken);
    }

    public async Task<LedgerAccountInterestAccrualResult> ApplyDueInterestAsync(
        IReadOnlyCollection<LedgerAccount>? loadedAccounts,
        CancellationToken cancellationToken = default)
    {
        var accounts = loadedAccounts?.ToList()
            ?? await _context.LedgerAccounts.ToListAsync(cancellationToken);
        for (var index = 0; index < accounts.Count; index++)
        {
            var account = accounts[index];
            var tracked = _context.ChangeTracker.Entries<LedgerAccount>()
                .FirstOrDefault(entry => entry.Entity.Id == account.Id);
            if (tracked is not null)
            {
                accounts[index] = tracked.Entity;
            }
            else if (_context.Entry(account).State == EntityState.Detached)
            {
                _context.LedgerAccounts.Attach(account);
            }
        }
        var candidates = accounts
            .Where(account => account.InterestEnabled
                && !account.IsArchived
                && account.InterestNextAccrualDate.HasValue)
            .ToList();
        if (candidates.Count == 0)
            return new(null);

        var today = _clock.Today;
        DateOnly? earliestPostedDate = null;
        while (true)
        {
            var dueDate = candidates
                .Where(account => account.InterestNextAccrualDate <= today)
                .Select(account => account.InterestNextAccrualDate!.Value)
                .OrderBy(date => date)
                .FirstOrDefault();
            if (dueDate == default)
                break;

            var dueAccounts = candidates
                .Where(account => account.InterestNextAccrualDate == dueDate)
                .ToList();
            var balances = await _balanceService.GetBalancesAsync(
                accounts,
                cancellationToken,
                TransactionDate.ExclusiveEndOfDate(dueDate));

            foreach (var account in dueAccounts)
            {
                var balance = balances.GetValueOrDefault(account.Id);
                var accrual = CalculateAccrual(
                    balance,
                    account.InterestRatePercent,
                    account.InterestFrequency,
                    account.InterestRemainder);
                account.InterestRemainder = accrual.Remainder;

                if (accrual.PostedAmount > 0m)
                {
                    var id = InterestTransactionId(account.Id, dueDate);
                    var existing = await _context.Transactions.FindAsync([id], cancellationToken);
                    if (existing is null)
                    {
                        _context.Transactions.Add(new Transaction
                        {
                            Id = id,
                            Date = TransactionDate.StartOfDate(dueDate),
                            PostedAt = DateTime.UtcNow,
                            Description = $"Interest earned - {account.Name}",
                            Category = "Interest",
                            LedgerCategory = account.Bucket,
                            Amount = accrual.PostedAmount,
                            ExcludeFromAutocomplete = true,
                            AccountId = account.Id,
                            StabilityReloadIntent = StabilityReloadIntent.Unanswered,
                        });
                    }
                    earliestPostedDate = earliestPostedDate is null
                        ? dueDate
                        : dueDate < earliestPostedDate.Value ? dueDate : earliestPostedDate.Value;
                }

                account.InterestNextAccrualDate = LedgerAccountInterestFrequency.NextDate(
                    dueDate,
                    account.InterestFrequency);
            }

            // Saving each calendar date makes the next balance query include earlier compounded
            // credits while keeping a long offline catch-up bounded and restart-safe.
            await _context.SaveChangesAsync(cancellationToken);
        }

        return new(earliestPostedDate);
    }

    public static string InterestTransactionId(string accountId, DateOnly date) =>
        $"{accountId}-interest-{date:yyyyMMdd}";

    public static InterestAccrual CalculateAccrual(
        decimal balance,
        decimal annualRatePercent,
        string frequency,
        decimal remainder)
    {
        if (balance <= 0m || annualRatePercent <= 0m)
            return new(0m, 0m);

        var annualFraction = annualRatePercent / 100m;
        var periodsPerYear = frequency switch
        {
            LedgerAccountInterestFrequency.Daily => 365m,
            LedgerAccountInterestFrequency.Monthly => 12m,
            LedgerAccountInterestFrequency.Yearly => 1m,
            _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unsupported interest frequency."),
        };
        var earned = (balance * annualFraction / periodsPerYear) + Math.Max(0m, remainder);
        var posted = Math.Floor((earned + 0.00000001m) * 100m) / 100m;
        return new(posted, Math.Round(earned - posted, 8, MidpointRounding.AwayFromZero));
    }

    public sealed record InterestAccrual(decimal PostedAmount, decimal Remainder);
}
