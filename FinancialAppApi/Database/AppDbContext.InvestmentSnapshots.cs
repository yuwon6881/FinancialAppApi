using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Database;

/// <summary>
/// Investment month-end snapshots are a cache over activity, cash movements, and how each
/// investment is classified and priced. Invalidating them here, in the same save as the write,
/// covers every write path -- controller, offline-sync replay, AI action -- and cannot be
/// forgotten by a new one. A change dated D removes every month ending on or after D.
/// </summary>
public partial class AppDbContext
{
    private Dictionary<string, DateOnly> EarliestInvestmentChanges()
    {
        var earliest = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        void Take(string userId, DateOnly date)
        {
            if (string.IsNullOrWhiteSpace(userId)) return;
            if (!earliest.TryGetValue(userId, out var current) || date < current) earliest[userId] = date;
        }

        foreach (var entry in ChangeTracker.Entries<InvestmentTransaction>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var userId = entry.Entity.UserId is { Length: > 0 } owner ? owner : CurrentUserId ?? string.Empty;
            if (entry.State != EntityState.Added)
                Take(userId, entry.OriginalValues.GetValue<DateOnly>(nameof(InvestmentTransaction.TradeDate)));
            if (entry.State != EntityState.Deleted) Take(userId, entry.Entity.TradeDate);
        }

        foreach (var entry in ChangeTracker.Entries<InvestmentCashFlow>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var userId = entry.Entity.UserId is { Length: > 0 } owner ? owner : CurrentUserId ?? string.Empty;
            if (entry.State != EntityState.Added)
                Take(userId, entry.OriginalValues.GetValue<DateOnly>(nameof(InvestmentCashFlow.Date)));
            if (entry.State != EntityState.Deleted) Take(userId, entry.Entity.Date);
        }

        // Reclassifying or repricing an investment changes every month it was held.
        foreach (var entry in ChangeTracker.Entries<InvestmentInstrument>()
                     .Where(entry => entry.State == EntityState.Deleted ||
                                     (entry.State == EntityState.Modified &&
                                      (entry.Property(nameof(InvestmentInstrument.AllocationSleeve)).IsModified ||
                                       entry.Property(nameof(InvestmentInstrument.Currency)).IsModified ||
                                       entry.Property(nameof(InvestmentInstrument.ProviderSymbol)).IsModified ||
                                       entry.Property(nameof(InvestmentInstrument.ProviderMic)).IsModified))))
        {
            Take(entry.Entity.UserId, DateOnly.MinValue);
        }
        foreach (var entry in ChangeTracker.Entries<InvestmentInstrumentMarketMapping>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            Take(entry.Entity.UserId, DateOnly.MinValue);
        }
        return earliest;
    }

    private void InvalidateInvestmentSnapshots()
    {
        foreach (var (userId, from) in EarliestInvestmentChanges())
        {
            InvestmentPeriodSnapshots.RemoveRange(InvestmentPeriodSnapshots.IgnoreQueryFilters()
                .Where(snapshot => snapshot.UserId == userId && snapshot.PeriodEnd >= from)
                .ToList());
        }
    }

    private async Task InvalidateInvestmentSnapshotsAsync(CancellationToken cancellationToken)
    {
        foreach (var (userId, from) in EarliestInvestmentChanges())
        {
            InvestmentPeriodSnapshots.RemoveRange(await InvestmentPeriodSnapshots.IgnoreQueryFilters()
                .Where(snapshot => snapshot.UserId == userId && snapshot.PeriodEnd >= from)
                .ToListAsync(cancellationToken));
        }
    }
}
