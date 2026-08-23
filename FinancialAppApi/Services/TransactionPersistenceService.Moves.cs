using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public sealed record TransactionMoveRequest(string Id, string TargetDate);
public sealed record TransactionBulkMoveResult(
    TransactionMutationStatus Status,
    IReadOnlyList<Transaction> Transactions,
    string? Message = null,
    IReadOnlyList<string>? AffectedDates = null);

public partial class TransactionPersistenceService
{
    public async Task<TransactionBulkMoveResult> MoveTransactionsAsync(
        IReadOnlyList<TransactionMoveRequest> moves,
        CancellationToken cancellationToken = default)
    {
        if (moves.Count is < 1 or > 100)
            return ConflictMove("Move between 1 and 100 transactions at a time.");

        var normalized = new List<(string Id, DateOnly Date)>();
        foreach (var move in moves)
        {
            var id = move.Id?.Trim() ?? string.Empty;
            if (id.Length == 0 || id.Contains("-split-", StringComparison.Ordinal))
                return ConflictMove("Generated income-split rows can only move with their parent transaction.");
            if (!TransactionDate.TryParseInputDate(move.TargetDate, out var date))
                return ConflictMove("Choose a valid destination date.");
            normalized.Add((id, date));
        }
        if (normalized.Select(move => move.Id).Distinct(StringComparer.Ordinal).Count() != normalized.Count)
            return ConflictMove("Each transaction may appear only once in a move request.");

        var ids = normalized.Select(move => move.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var loanIds = await LoanRepaymentActionTransactionIdsAsync(cancellationToken);
        var result = new TransactionBulkMoveResult(TransactionMutationStatus.Conflict, [], "Move failed.");
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            if (_context.Database.IsNpgsql())
            {
                foreach (var id in ids)
                {
                    await _context.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_xact_lock(hashtext({"transaction:" + id}))",
                        cancellationToken);
                }
            }
            var parents = await _context.Transactions
                .Where(transaction => ids.Contains(transaction.Id))
                .OrderBy(transaction => transaction.Id)
                .ToListAsync(cancellationToken);
            if (parents.Count != ids.Length)
            {
                result = ConflictMove("One or more transactions no longer exist. Nothing was moved.");
                return;
            }

            foreach (var transaction in parents)
            {
                var reason = MoveIneligibilityReason(transaction, loanIds);
                if (reason is not null)
                {
                    result = ConflictMove(reason);
                    return;
                }
            }

            var children = new List<Transaction>();
            foreach (var parent in parents.Where(transaction => IsIncomeLedgerCategory(transaction.LedgerCategory)))
            {
                var prefix = parent.Id + "-split-";
                children.AddRange(await _context.Transactions
                    .Where(transaction => transaction.Id.StartsWith(prefix))
                    .ToListAsync(cancellationToken));
            }
            var affectedDates = new HashSet<string>(StringComparer.Ordinal);
            var moved = new List<Transaction>(parents.Count + children.Count);
            foreach (var parent in parents)
            {
                var target = normalized.Single(move => move.Id == parent.Id).Date;
                affectedDates.Add(TransactionDate.ToDateOnly(parent.Date).ToString("yyyy-MM-dd"));
                affectedDates.Add(target.ToString("yyyy-MM-dd"));
                parent.Date = TransactionDate.FromInputDate(target);
                moved.Add(parent);
                foreach (var child in children.Where(child => child.Id.StartsWith(parent.Id + "-split-", StringComparison.Ordinal)))
                {
                    child.Date = TransactionDate.FromInputDate(target);
                    moved.Add(child);
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
            var earliest = affectedDates.Min(StringComparer.Ordinal)!;
            await InvalidateCycleBalancesFromAsync(TransactionDate.FromInputDate(DateOnly.ParseExact(earliest, "yyyy-MM-dd")), cancellationToken);
            await dbTransaction.CommitAsync(cancellationToken);
            result = new TransactionBulkMoveResult(
                TransactionMutationStatus.Updated,
                moved,
                AffectedDates: affectedDates.OrderBy(date => date, StringComparer.Ordinal).ToArray());
        });
        return result;
    }

    private static string? MoveIneligibilityReason(Transaction transaction, HashSet<string> loanIds)
    {
        if (!string.IsNullOrWhiteSpace(transaction.RecurringPaymentId)) return "Recurring settlements must be changed through their recurring-payment workflow.";
        if (loanIds.Contains(transaction.Id)) return "Loan repayment records must be changed through their loan workflow.";
        if (transaction.SavingsGoalId.HasValue) return "Commitment completion records cannot be moved.";
        if (transaction.WishlistItemId.HasValue) return "Reward completion records cannot be moved.";
        if (transaction.IsAccountBalanceAdjustment) return "Reconciliation adjustments cannot be moved.";
        if (transaction.StabilityRecoveryTopUpAmount is > 0m) return "Stability lifecycle records cannot be moved.";
        return null;
    }

    private static TransactionBulkMoveResult ConflictMove(string message) =>
        new(TransactionMutationStatus.Conflict, [], message);
}
