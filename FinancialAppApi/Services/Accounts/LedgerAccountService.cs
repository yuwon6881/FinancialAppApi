using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Accounts;

public enum LedgerAccountMutationStatus
{
    Success,
    NotFound,
    Invalid,
    Conflict,
}

public sealed record LedgerAccountMutation(
    string Id,
    string Name,
    string Bucket,
    string Kind,
    bool IsArchived,
    decimal OpeningAmount = 0m);

public sealed record LedgerAccountMutationResult(
    LedgerAccountMutationStatus Status,
    LedgerAccount? Account = null,
    string? Message = null,
    int ActivityCount = 0);

public sealed record LedgerAccountReconcileTarget(
    string? Id,
    string Name,
    string? Kind,
    bool IsArchived,
    decimal ExpectedCurrent,
    decimal Target,
    string? ExpectedName = null,
    string? ExpectedKind = null,
    bool? ExpectedIsArchived = null);

public sealed record LedgerAccountReconcileRequest(
    string OperationId,
    string Bucket,
    decimal ExpectedBucketTotal,
    IReadOnlyList<LedgerAccountReconcileTarget> Targets,
    string? Description = null);

public sealed record LedgerAccountReconcileTransaction(
    string Id,
    DateTime Date,
    string Description,
    string Category,
    string LedgerCategory,
    decimal Amount,
    string? AccountId,
    string? CounterAccountId,
    bool IsAccountBalanceAdjustment = false,
    string? StabilityReloadIntent = null);

public sealed record LedgerAccountReconcileResult(
    LedgerAccountMutationStatus Status,
    string? Message = null,
    IReadOnlyList<LedgerAccount>? Accounts = null,
    IReadOnlyList<LedgerAccountReconcileTransaction>? Transactions = null);

public sealed partial class LedgerAccountService
{
    private readonly AppDbContext _context;
    private readonly LedgerAccountBalanceService _balanceService;
    private readonly CycleBalanceService _cycleBalanceService;
    private readonly FinancialClock _clock;

    public LedgerAccountService(
        AppDbContext context,
        LedgerAccountBalanceService balanceService,
        CycleBalanceService cycleBalanceService,
        FinancialClock? clock = null)
    {
        _context = context;
        _balanceService = balanceService;
        _cycleBalanceService = cycleBalanceService;
        _clock = clock ?? FinancialClock.Utc;
    }

    public async Task<IReadOnlyList<LedgerAccount>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
        await _context.LedgerAccounts
            .AsNoTracking()
            .OrderBy(account => account.Bucket)
            .ThenBy(account => account.Name)
            .ThenBy(account => account.Id)
            .ToListAsync(cancellationToken);

    public Task<IReadOnlyDictionary<string, decimal>> GetBalancesAsync(
        IReadOnlyCollection<LedgerAccount> accounts,
        CancellationToken cancellationToken = default,
        DateTime? throughExclusive = null) =>
        _balanceService.GetBalancesAsync(accounts, cancellationToken, throughExclusive);

    public Task<LedgerAccountBalanceSnapshot> GetBalanceSnapshotAsync(
        IReadOnlyCollection<LedgerAccount> accounts,
        DateTime throughExclusive,
        CancellationToken cancellationToken = default) =>
        _balanceService.GetBalanceSnapshotAsync(accounts, throughExclusive, cancellationToken);

    public async Task<LedgerAccountMutationResult> CreateAsync(
        LedgerAccountMutation mutation,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(mutation);
        if (validation is not null) return Invalid(validation);

        var id = string.IsNullOrWhiteSpace(mutation.Id)
            ? $"acct-{Guid.NewGuid():N}"
            : mutation.Id.Trim();
        var existing = await _context.LedgerAccounts.FirstOrDefaultAsync(account => account.Id == id, cancellationToken);
        if (existing != null)
        {
            var sameShape = string.Equals(existing.Name, mutation.Name.Trim(), StringComparison.Ordinal)
                && string.Equals(existing.Bucket, NormalizeBucket(mutation.Bucket), StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Kind, LedgerAccountKind.Normalize(mutation.Kind), StringComparison.OrdinalIgnoreCase);
            return sameShape
                ? new LedgerAccountMutationResult(LedgerAccountMutationStatus.Success, existing)
                : Conflict("An account with this id already exists.");
        }
        if (await _context.LedgerAccounts.AnyAsync(
                account => account.Name.ToLower() == mutation.Name.Trim().ToLower(),
                cancellationToken))
            return Conflict("An account with this name already exists.");

        var now = DateTime.UtcNow;
        var account = new LedgerAccount
        {
            Id = id,
            Name = mutation.Name.Trim(),
            Bucket = NormalizeBucket(mutation.Bucket),
            Kind = LedgerAccountKind.Normalize(mutation.Kind),
            IsArchived = false,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _context.LedgerAccounts.Add(account);
        if (mutation.OpeningAmount != 0m)
        {
            _context.Transactions.Add(new Transaction
            {
                Id = $"{account.Id}-opening",
                Date = TransactionDate.StartOfDate(_clock.Today),
                PostedAt = now,
                Description = $"Opening Balance — {account.Name}",
                Category = "Adjustment",
                LedgerCategory = account.Bucket,
                Amount = MoneyRounding.RoundMoney(mutation.OpeningAmount),
                ExcludeFromAutocomplete = true,
                IsAccountBalanceAdjustment = true,
                AccountId = account.Id,
                StabilityReloadIntent = StabilityReloadIntent.NotRequired,
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
        if (mutation.OpeningAmount != 0m)
        {
            var (year, month) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(
                _clock.Today,
                await GetCycleDayAsync(cancellationToken));
            await _cycleBalanceService.InvalidateFromAsync(year, month, cancellationToken);
        }
        return new LedgerAccountMutationResult(LedgerAccountMutationStatus.Success, account);
    }

    public async Task<LedgerAccountMutationResult> UpdateAsync(
        string id,
        LedgerAccountMutation mutation,
        CancellationToken cancellationToken = default)
    {
        var account = await _context.LedgerAccounts.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (account is null) return new(LedgerAccountMutationStatus.NotFound);
        var validation = Validate(mutation);
        if (validation is not null) return Invalid(validation);
        if (await _context.LedgerAccounts.AnyAsync(
                candidate => candidate.Id != id && candidate.Name.ToLower() == mutation.Name.Trim().ToLower(),
                cancellationToken))
            return Conflict("An account with this name already exists.");

        var nextBucket = NormalizeBucket(mutation.Bucket);
        var activityCount = await _context.Transactions.CountAsync(
            transaction => transaction.AccountId == id || transaction.CounterAccountId == id,
            cancellationToken);
        if (!string.Equals(account.Bucket, nextBucket, StringComparison.OrdinalIgnoreCase) && activityCount > 0)
        {
            return new(
                LedgerAccountMutationStatus.Conflict,
                account,
                "An account with ledger activity cannot move to another bucket.",
                activityCount);
        }

        var oldBucket = account.Bucket;
        var bucketChanged = !string.Equals(oldBucket, nextBucket, StringComparison.OrdinalIgnoreCase);
        var recurringUsesAccount = await _context.RecurringPayments.AnyAsync(
            payment => payment.AccountId == id,
            cancellationToken);
        if (bucketChanged && recurringUsesAccount)
            return Conflict("A recurring payment uses this account. Reassign it before moving the account.");
        if (mutation.IsArchived && !account.IsArchived && await _context.RecurringPayments.AnyAsync(
                    payment => payment.AccountId == id && payment.Active,
                cancellationToken))
            return Conflict("An active recurring payment uses this account. Reassign it before closing the account.");
        if (bucketChanged && !account.IsArchived)
        {
            var hasOtherLiveAccount = await _context.LedgerAccounts.AnyAsync(
                candidate => candidate.Id != id
                    && candidate.Bucket == account.Bucket
                    && !candidate.IsArchived,
                cancellationToken);
            if (!hasOtherLiveAccount)
                return Conflict("Every bucket needs one open account. Add another before moving this one.");
        }
        if (mutation.IsArchived && !account.IsArchived)
        {
            var hasOtherLiveAccount = await _context.LedgerAccounts.AnyAsync(
                candidate => candidate.Id != id
                    && candidate.Bucket == account.Bucket
                    && !candidate.IsArchived,
                cancellationToken);
            if (!hasOtherLiveAccount)
                return Conflict("Every bucket needs one open account. Add another before closing this one.");
        }
        account.Name = mutation.Name.Trim();
        account.Bucket = nextBucket;
        account.Kind = LedgerAccountKind.Normalize(mutation.Kind);
        account.IsArchived = mutation.IsArchived;
        account.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return new(LedgerAccountMutationStatus.Success, account);
    }

    public async Task<LedgerAccountMutationResult> DeleteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var account = await _context.LedgerAccounts.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (account is null) return new(LedgerAccountMutationStatus.NotFound);
        var activityCount = await _context.Transactions.CountAsync(
            transaction => transaction.AccountId == id || transaction.CounterAccountId == id,
            cancellationToken);
        if (activityCount > 0)
        {
            return new(
                LedgerAccountMutationStatus.Conflict,
                account,
                $"This account has {activityCount} ledger transaction{(activityCount == 1 ? "" : "s")}. Archive it instead of deleting it.",
                activityCount);
        }

        if (await _context.RecurringPayments.AnyAsync(payment => payment.AccountId == id, cancellationToken))
            return Conflict("A recurring payment uses this account. Reassign it before deleting the account.");
        if (!account.IsArchived && !await _context.LedgerAccounts.AnyAsync(
                candidate => candidate.Id != id
                    && candidate.Bucket == account.Bucket
                    && !candidate.IsArchived,
                cancellationToken))
            return Conflict("Every bucket needs one open account. Add another before deleting this one.");

        _context.LedgerAccounts.Remove(account);
        await _context.SaveChangesAsync(cancellationToken);
        return new(LedgerAccountMutationStatus.Success, account);
    }

    private async Task<int> GetCycleDayAsync(CancellationToken cancellationToken) =>
        (await _context.FinancialSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken))?.CycleDay
        ?? FinancialConstants.DefaultCycleDay;

    private static string? Validate(LedgerAccountMutation mutation)
    {
        if (string.IsNullOrWhiteSpace(mutation.Name) || mutation.Name.Trim().Length > 200)
            return "Account name is required and must be 200 characters or fewer.";
        if (!FinancialConstants.BudgetCategories.Contains(mutation.Bucket, StringComparer.OrdinalIgnoreCase))
            return $"Account bucket must be one of: {string.Join(", ", FinancialConstants.BudgetCategories)}.";
        if (!LedgerAccountKind.IsValid(mutation.Kind))
            return $"Account type must be one of: {string.Join(", ", LedgerAccountKind.Values)}.";
        if (mutation.OpeningAmount is < -9999999999.99m or > 9999999999.99m)
            return "Opening amount is outside the supported money range.";
        return null;
    }

    private static string NormalizeBucket(string value) =>
        FinancialConstants.BudgetCategories.First(bucket => bucket.Equals(value, StringComparison.OrdinalIgnoreCase));

    private static LedgerAccountMutationResult Invalid(string message) =>
        new(LedgerAccountMutationStatus.Invalid, Message: message);

    private static LedgerAccountMutationResult Conflict(string message) =>
        new(LedgerAccountMutationStatus.Conflict, Message: message);
}
