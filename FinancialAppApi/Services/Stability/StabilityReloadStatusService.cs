using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services.Stability;

/// <summary>
/// Produces the per-withdrawal status used by Ledger rows. The status is derived from the same full
/// FIFO replay as the dashboard, while plan revisions make target attainment durable across later
/// target edits.
/// </summary>
public sealed class StabilityReloadStatusService
{
    private readonly AppDbContext _context;
    private readonly StabilityPlanRevisionService _planRevisionService;

    public StabilityReloadStatusService(
        AppDbContext context,
        StabilityPlanRevisionService? planRevisionService = null)
    {
        _context = context;
        _planRevisionService = planRevisionService ?? new StabilityPlanRevisionService(context);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetStatusMapAsync(
        CancellationToken cancellationToken = default)
    {
        var setting = await _context.FinancialSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        if (setting == null) return new Dictionary<string, string>(StringComparer.Ordinal);

        var transactions = await _context.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.LedgerCategory != "Discarded")
            .OrderBy(transaction => transaction.Date)
            .ThenBy(transaction => transaction.PostedAt)
            .ThenBy(transaction => transaction.Id)
            .ToListAsync(cancellationToken);
        var revisions = await _planRevisionService.GetAsync(setting, cancellationToken);
        var movements = StabilityReloadLedger.DescribeAll(
            transactions,
            transaction => StabilityPlanRevisionService.At(
                revisions,
                transaction.PostedAt).StabilityAlloc);
        var state = StabilityReloadLedger.Replay(
            new ReloadState(0m, null, 0m, 0m),
            openingBalance: 0m,
            revisions.Select(revision => new ReloadPlanPoint(
                revision.EffectiveAt,
                revision.TargetStabilityFund)),
            movements);
        var obligations = state.Obligations?.ToDictionary(
            obligation => obligation.TransactionId,
            StringComparer.Ordinal)
            ?? new Dictionary<string, ReloadObligation>(StringComparer.Ordinal);
        var transactionsById = transactions.ToDictionary(
            transaction => transaction.Id,
            StringComparer.Ordinal);
        var status = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var movement in movements.Where(item => item.Change < 0m && item.TransactionId is not null))
        {
            var id = movement.TransactionId!;
            if (!transactionsById.TryGetValue(id, out var transaction)) continue;
            if (transaction.IsAccountBalanceAdjustment) continue;
            var intent = StabilityReloadIntent.Normalize(transaction.StabilityReloadIntent);
            if (intent == StabilityReloadIntent.NotRequired)
            {
                status[id] = StabilityReloadStatus.NotRequired;
                continue;
            }

            var original = Math.Max(0m, -movement.Change);
            var remaining = obligations.TryGetValue(id, out var obligation)
                ? Math.Clamp(obligation.RemainingAmount, 0m, original)
                : original;
            status[id] = remaining <= 0m
                ? StabilityReloadStatus.Complete
                : remaining < original
                    ? StabilityReloadStatus.PartlyRepaid
                    : StabilityReloadStatus.Outstanding;
        }

        return status;
    }
}
