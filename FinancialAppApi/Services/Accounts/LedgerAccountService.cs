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
    bool IsDefault,
    decimal OpeningAmount = 0m);

public sealed record LedgerAccountMutationResult(
    LedgerAccountMutationStatus Status,
    LedgerAccount? Account = null,
    string? Message = null,
    int ActivityCount = 0);

public sealed class LedgerAccountService
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
            IsDefault = mutation.IsDefault || !await HasLiveDefaultAsync(mutation.Bucket, cancellationToken),
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (account.IsDefault)
            await ClearDefaultAsync(account.Bucket, null, cancellationToken);

        _context.LedgerAccounts.Add(account);
        if (mutation.OpeningAmount != 0m)
        {
            _context.Transactions.Add(new Transaction
            {
                Id = $"{account.Id}-opening",
                Date = _clock.Today.ToDateTime(TimeOnly.MinValue),
                PostedAt = now,
                Description = $"Opening Balance — {account.Name}",
                Category = "Adjustment",
                LedgerCategory = account.Bucket,
                Amount = Math.Round(mutation.OpeningAmount, 2, MidpointRounding.AwayFromZero),
                AccountId = account.Id,
                StabilityReloadIntent = StabilityReloadIntent.Unanswered,
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
        var wasDefault = account.IsDefault;
        account.Name = mutation.Name.Trim();
        account.Bucket = nextBucket;
        account.Kind = LedgerAccountKind.Normalize(mutation.Kind);
        account.IsArchived = mutation.IsArchived;
        account.UpdatedAt = DateTime.UtcNow;

        if (mutation.IsDefault && !account.IsArchived)
        {
            await ClearDefaultAsync(nextBucket, id, cancellationToken);
            account.IsDefault = true;
        }
        else
        {
            account.IsDefault = false;
        }

        if (wasDefault || !string.Equals(oldBucket, nextBucket, StringComparison.OrdinalIgnoreCase))
            await EnsureDefaultAsync(oldBucket, string.Equals(oldBucket, nextBucket, StringComparison.OrdinalIgnoreCase) ? id : null, cancellationToken);
        if (!account.IsDefault)
        {
            await EnsureDefaultAsync(nextBucket, id, cancellationToken);
            if (!account.IsArchived && !await HasLiveDefaultAsync(nextBucket, cancellationToken))
                account.IsDefault = true;
        }

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

        _context.LedgerAccounts.Remove(account);
        await EnsureDefaultAsync(account.Bucket, id, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return new(LedgerAccountMutationStatus.Success, account);
    }

    private async Task<bool> HasLiveDefaultAsync(string bucket, CancellationToken cancellationToken)
    {
        var candidates = await _context.LedgerAccounts
            .Where(account => account.Bucket == NormalizeBucket(bucket))
            .ToListAsync(cancellationToken);
        return candidates.Any(account => account.IsDefault && !account.IsArchived);
    }

    private async Task ClearDefaultAsync(string bucket, string? exceptId, CancellationToken cancellationToken)
    {
        var defaults = await _context.LedgerAccounts
            .Where(account => account.Bucket == NormalizeBucket(bucket)
                && account.IsDefault
                && (exceptId == null || account.Id != exceptId))
            .ToListAsync(cancellationToken);
        foreach (var account in defaults) account.IsDefault = false;
    }

    private async Task EnsureDefaultAsync(string bucket, string? exceptId, CancellationToken cancellationToken)
    {
        var candidates = (await _context.LedgerAccounts
                .Where(account => account.Bucket == NormalizeBucket(bucket))
                .ToListAsync(cancellationToken))
            .Where(account => (exceptId == null || account.Id != exceptId) && !account.IsArchived)
            .OrderBy(account => account.CreatedAt)
            .ThenBy(account => account.Id)
            .ToList();
        if (candidates.Any(account => account.IsDefault)) return;
        var replacement = candidates.FirstOrDefault();
        if (replacement is null) return;
        replacement.IsDefault = true;
        replacement.UpdatedAt = DateTime.UtcNow;
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
