using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services;

public static class RecurringAccountShortfallEvaluator
{
    // cycleTransactions supplies what a partially paid occurrence has already taken; without it a
    // partial is measured at its full scheduled amount and warns about money that has already left.
    public static List<object> EvaluateShortfalls(
        IReadOnlyList<RecurringPaymentOccurrence> occurrences,
        IReadOnlyList<RecurringPayment> allRecurring,
        IReadOnlyList<LedgerAccount> accounts,
        IReadOnlyDictionary<string, decimal> accountBalances,
        DateOnly today,
        IReadOnlyCollection<Transaction>? cycleTransactions = null)
    {
        var paidByOccurrence = RecurringOccurrenceAmounts.PaidByOccurrence(cycleTransactions);
        var accountsById = accounts.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var parentById = allRecurring.ToDictionary(payment => payment.Id, StringComparer.Ordinal);
        var eligible = occurrences
            // Unresolved, not just Pending: a partially paid auto-deduct bill still has a balance the
            // bank will take, and filtering it out here dropped it from the dashboard warning while
            // the push path still reported it — the two are meant to answer the same question.
            .Where(occurrence => RecurringOccurrenceStatus.IsUnresolved(occurrence.Status))
            .Select(occurrence =>
            {
                var parent = parentById.GetValueOrDefault(occurrence.RecurringPaymentId);
                var accountId = occurrence.AccountId ?? parent?.AccountId;
                return new { Occurrence = occurrence, Parent = parent, AccountId = accountId };
            })
            .Where(item => item.Parent?.PaymentMode == RecurringPaymentMode.AutoDeduct
                && !string.IsNullOrWhiteSpace(item.AccountId))
            .Select(item => new
            {
                item.Occurrence,
                item.Parent,
                AccountId = item.AccountId!,
                Amount = RecurringOccurrenceAmounts.Outstanding(item.Occurrence, item.Parent!.Amount, paidByOccurrence),
                OffsetDays = item.Occurrence.OccurrenceDate.DayNumber - today.DayNumber
            })
            .Where(item => item.OffsetDays is >= 0 and <= 31 && item.Amount > 0m)
            .ToList();
        var projections = RecurringAccountBalanceProjection.Project(
            eligible.Select(item => new RecurringAccountDebit(
                item.Occurrence.Id,
                item.AccountId,
                item.Occurrence.OccurrenceDate,
                item.Amount)),
            accountBalances);
        var shortfalls = new List<(int OffsetDays, object Shortfall)>();

        foreach (var item in eligible)
        {
            var occ = item.Occurrence;
            var projection = projections[occ.Id];
            if (projection.Shortfall <= 0m) continue;

            var account = accountsById.GetValueOrDefault(item.AccountId);
            shortfalls.Add((item.OffsetDays, new
            {
                recurringPaymentId = occ.RecurringPaymentId,
                name = occ.Name,
                amount = ObfuscationHelper.Obfuscate(item.Amount),
                dueDate = occ.OccurrenceDate.ToString("yyyy-MM-dd"),
                dueDay = occ.OccurrenceDate.Day,
                offsetDays = item.OffsetDays,
                accountId = item.AccountId,
                accountName = account?.Name ?? "Account",
                accountBalance = ObfuscationHelper.Obfuscate(projection.BalanceBefore),
                shortfall = ObfuscationHelper.Obfuscate(projection.Shortfall)
            }));
        }

        return shortfalls.OrderBy(item => item.OffsetDays).Select(item => item.Shortfall).ToList();
    }
}
