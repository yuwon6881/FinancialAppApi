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
    private string? _categoryName;

    private const string CategoryNameLower = "interest";

    /// <summary>
    /// The transaction category every interest credit is filed under. It is provisioned on demand
    /// rather than seeded, because <see cref="DbSeeder"/> deliberately never tops up individual
    /// missing names — a re-seed would resurrect defaults the user deleted on purpose. Enabling
    /// interest is an explicit user action, so creating the one category its rows need is a
    /// provisioning step for that action, not a re-seed.
    /// </summary>
    public const string CategoryName = "Interest";

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

            // Interest is an "as known at the time" ledger record. A later transaction dated before
            // this posting does not rewrite the deterministic interest row or reopen a completed
            // period; only future periods observe the corrected balance. Replaying history here
            // would make a backdated edit mutate already-reported cycles and break idempotency.
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
                        var categoryName = await EnsureCategoryAsync(cancellationToken);
                        _context.Transactions.Add(new Transaction
                        {
                            Id = id,
                            Date = TransactionDate.StartOfDate(dueDate),
                            PostedAt = DateTime.UtcNow,
                            Description = $"Interest earned - {account.Name}",
                            Category = categoryName,
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
                    account.InterestFrequency,
                    account.InterestAnchorDay);
            }

            // Saving each calendar date makes the next balance query include earlier compounded
            // credits while keeping a long offline catch-up bounded and restart-safe.
            await _context.SaveChangesAsync(cancellationToken);
        }

        return new(earliestPostedDate);
    }

    public static string InterestTransactionId(string accountId, DateOnly date) =>
        $"{accountId}-interest-{date:yyyyMMdd}";

    /// <summary>
    /// Interest rows used to be filed under a category name no user actually had, so they showed
    /// up in reports as a category that was missing from Settings, from the category filters and
    /// from <c>/api/categories/usage</c>. The category is inflow-only: interest is money coming
    /// in, and a spending guide over it would be meaningless.
    /// </summary>
    private async Task<string> EnsureCategoryAsync(CancellationToken cancellationToken)
    {
        if (_categoryName is not null) return _categoryName;

        var existing = await _context.TransactionCategories
            .FirstOrDefaultAsync(category => category.Name.ToLower() == CategoryNameLower, cancellationToken);
        if (existing is not null)
        {
            _categoryName = existing.Name;
            return _categoryName;
        }

        _context.TransactionCategories.Add(new TransactionCategory
        {
            Id = $"cat-{_context.RequireCurrentUserId()}-interest",
            Name = CategoryName,
            Type = CategoryFlowType.Inflow,
        });
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            _categoryName = CategoryName;
            return _categoryName;
        }
        catch
        {
            _categoryName = null;
            throw;
        }
    }

    public static InterestAccrual CalculateAccrual(
        decimal balance,
        decimal annualRatePercent,
        string frequency,
        decimal remainder)
    {
        // A period that earns nothing must still *carry* what earlier periods banked. Returning a
        // zero remainder here quietly destroyed the accumulated sub-cent whenever the balance
        // touched zero for a day, so a low-balance account could earn fractions for weeks and post
        // nothing. Only the earning is skipped; the carry is not the account's to lose.
        if (balance <= 0m || annualRatePercent <= 0m)
            return new(0m, Math.Max(0m, remainder));

        var annualFraction = annualRatePercent / 100m;
        var periodsPerYear = frequency switch
        {
            LedgerAccountInterestFrequency.Daily => 365m,
            LedgerAccountInterestFrequency.Monthly => 12m,
            LedgerAccountInterestFrequency.Yearly => 1m,
            _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unsupported interest frequency."),
        };
        // Daily uses Actual/365-Fixed: every calendar day contributes one 365th of the annual
        // rate, including leap years. Monthly and yearly remain the configured period rates.
        var earned = (balance * annualFraction / periodsPerYear) + Math.Max(0m, remainder);
        var posted = Math.Floor((earned + 0.00000001m) * 100m) / 100m;
        return new(posted, Math.Round(earned - posted, 8, MidpointRounding.AwayFromZero));
    }

    public sealed record InterestAccrual(decimal PostedAmount, decimal Remainder);
}
