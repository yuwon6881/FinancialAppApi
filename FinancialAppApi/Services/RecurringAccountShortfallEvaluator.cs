using FinancialAppApi.Database;
using FinancialAppApi.Models;

namespace FinancialAppApi.Services;

public static class RecurringAccountShortfallEvaluator
{
    public static List<object> EvaluateShortfalls(
        IReadOnlyList<RecurringPaymentOccurrence> occurrences,
        IReadOnlyList<RecurringPayment> allRecurring,
        IReadOnlyList<LedgerAccount> accounts,
        IReadOnlyDictionary<string, decimal> accountBalances,
        DateOnly today)
    {
        var accountsById = accounts.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var parentById = allRecurring.ToDictionary(payment => payment.Id, StringComparer.Ordinal);
        var eligible = occurrences
            .Where(occurrence => occurrence.Status == RecurringOccurrenceStatus.Pending)
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
                Amount = Math.Abs(item.Occurrence.ScheduledAmount ?? item.Parent!.Amount),
                OffsetDays = item.Occurrence.OccurrenceDate.DayNumber - today.DayNumber
            })
            .Where(item => item.OffsetDays is >= 0 and <= 31)
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
