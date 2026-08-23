using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using FinancialAppApi.Models;

namespace FinancialAppApi.Database;

public partial class AppDbContext
{
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        CaptureCategoryLimitAlertEvaluations();
        ApplyUserOwnership();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        _capturedCategoryLimitTransactions.Clear();
        return result;
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        CaptureCategoryLimitAlertEvaluations();
        ApplyUserOwnership();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        _capturedCategoryLimitTransactions.Clear();
        return result;
    }

    private void CaptureCategoryLimitAlertEvaluations()
    {
        if (SuppressCategoryLimitAlertCapture || CurrentUserId == null) return;

        // Capturing is a write per changed transaction row, so it is worth not doing for the
        // users and rows that provably cannot produce an alert. Two cheap tests, no extra query:
        // a FinancialSetting already tracked by this save answers the consent question (every
        // ledger write path loads one), and a row that is not an outflow can never cross a
        // spending guide on either side of the change. When no setting is tracked the capture
        // still happens -- failing towards a redundant row beats dropping a real crossing.
        var trackedSetting = ChangeTracker.Entries<FinancialSetting>().FirstOrDefault();
        if (trackedSetting != null && !trackedSetting.Entity.CategoryLimitAlertsEnabled) return;

        var transactionEntries = ChangeTracker.Entries<Transaction>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(entry => entry.State != EntityState.Modified ||
                entry.Property(nameof(Transaction.Category)).IsModified ||
                entry.Property(nameof(Transaction.LedgerCategory)).IsModified ||
                entry.Property(nameof(Transaction.Date)).IsModified ||
                entry.Property(nameof(Transaction.Amount)).IsModified)
            .Where(entry => TouchesAnOutflow(entry))
            .Where(entry => _capturedCategoryLimitTransactions.Add(entry.Entity))
            .ToList();

        foreach (var entry in transactionEntries)
        {
            HasCapturedCategoryLimitEvaluations = true;
            var hasPrevious = entry.State is EntityState.Modified or EntityState.Deleted;
            var hasCurrent = entry.State is EntityState.Added or EntityState.Modified;
            CategoryLimitAlertEvaluations.Add(new CategoryLimitAlertEvaluation
            {
                Id = $"clae-{Guid.NewGuid():N}",
                PreviousCategory = hasPrevious ? entry.OriginalValues.GetValue<string>(nameof(Transaction.Category)) : null,
                PreviousLedgerCategory = hasPrevious ? entry.OriginalValues.GetValue<string>(nameof(Transaction.LedgerCategory)) : null,
                PreviousDate = hasPrevious ? entry.OriginalValues.GetValue<DateTime>(nameof(Transaction.Date)) : null,
                PreviousAmount = hasPrevious ? entry.OriginalValues.GetValue<decimal>(nameof(Transaction.Amount)) : null,
                CurrentCategory = hasCurrent ? entry.Entity.Category : null,
                CurrentLedgerCategory = hasCurrent ? entry.Entity.LedgerCategory : null,
                CurrentDate = hasCurrent ? entry.Entity.Date : null,
                CurrentAmount = hasCurrent ? entry.Entity.Amount : null,
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    // Mirrors the processor's own filter (AddContribution ignores anything that is not a negative
    // amount): if neither side of this change is an outflow, no delta it could produce would ever
    // move a category's spending total.
    private static bool TouchesAnOutflow(EntityEntry<Transaction> entry)
    {
        var currentIsOutflow = entry.State is EntityState.Added or EntityState.Modified &&
            entry.Entity.Amount < 0;
        var previousIsOutflow = entry.State is EntityState.Modified or EntityState.Deleted &&
            entry.OriginalValues.GetValue<decimal>(nameof(Transaction.Amount)) < 0;
        return currentIsOutflow || previousIsOutflow;
    }

    private void ConfigureUserOwnership<TEntity>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity,
        bool applyQueryFilter)
        where TEntity : class, IUserOwnedEntity
    {
        entity.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        if (applyQueryFilter)
        {
            entity.HasQueryFilter(e => CurrentUserId != null && e.UserId == CurrentUserId);
        }
    }

    /// <summary>
    /// Currency codes carry an invariant -- uppercase ISO-4217 -- that used to be re-asserted
    /// ad hoc at each call site. Enforcing it once on write means a lowercase code can never
    /// reach a comparison that silently fails to match (e.g. FX resolution in the portfolio
    /// service, which returns null totals rather than an error when a lookup misses).
    /// </summary>
    private void NormalizeCurrencies()
    {
        static string Upper(string value) => value.Trim().ToUpperInvariant();

        foreach (var entry in ChangeTracker.Entries<InvestmentAccount>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.BaseCurrency = Upper(entry.Entity.BaseCurrency);
        }

        foreach (var entry in ChangeTracker.Entries<InvestmentInstrument>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.Currency = Upper(entry.Entity.Currency);
        }

        foreach (var entry in ChangeTracker.Entries<InvestmentCashFlow>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.Currency = Upper(entry.Entity.Currency);
            if (entry.Entity.ToCurrency is not null) entry.Entity.ToCurrency = Upper(entry.Entity.ToCurrency);
        }

        foreach (var entry in ChangeTracker.Entries<FxRateBar>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.BaseCurrency = Upper(entry.Entity.BaseCurrency);
            entry.Entity.QuoteCurrency = Upper(entry.Entity.QuoteCurrency);
        }

        foreach (var entry in ChangeTracker.Entries<FinancialSetting>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.Currency = Upper(entry.Entity.Currency);
        }
    }

    private void ApplyUserOwnership()
    {
        foreach (var entry in ChangeTracker.Entries<AppUser>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.Username = entry.Entity.Username.Trim();
            entry.Entity.NormalizedUsername = entry.Entity.Username.ToUpperInvariant();
        }

        NormalizeCurrencies();

        foreach (var entry in ChangeTracker.Entries<IUserOwnedEntity>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.State == EntityState.Added && string.IsNullOrWhiteSpace(entry.Entity.UserId))
            {
                entry.Entity.UserId = RequireCurrentUserId();
            }

            if (CurrentUserId != null && !string.Equals(entry.Entity.UserId, CurrentUserId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A request cannot write data owned by another user.");
            }

            if (string.IsNullOrWhiteSpace(entry.Entity.UserId))
            {
                throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} must have a user owner.");
            }
        }
    }
}
