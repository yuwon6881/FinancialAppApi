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
        var shortfalls = new List<(int OffsetDays, object Shortfall)>();

        foreach (var occ in occurrences)
        {
            if (occ.Status != RecurringOccurrenceStatus.Pending) continue;

            var parent = allRecurring.FirstOrDefault(p => p.Id == occ.RecurringPaymentId);
            var paymentMode = parent?.PaymentMode ?? RecurringPaymentMode.Manual;
            if (paymentMode != RecurringPaymentMode.AutoDeduct) continue;

            var accountId = occ.AccountId ?? parent?.AccountId;
            if (string.IsNullOrWhiteSpace(accountId)) continue;

            var account = accountsById.GetValueOrDefault(accountId);
            var accountBal = accountBalances.TryGetValue(accountId, out var b) ? b : 0m;
            var scheduledAmt = Math.Abs(occ.ScheduledAmount ?? parent?.Amount ?? 0m);
            if (accountBal >= scheduledAmt) continue;

            var offsetDays = occ.OccurrenceDate.DayNumber - today.DayNumber;
            // Include shortfalls due today or in upcoming cycle days
            if (offsetDays < 0 || offsetDays > 31) continue;

            var shortfall = scheduledAmt - accountBal;
            shortfalls.Add((offsetDays, new
            {
                recurringPaymentId = occ.RecurringPaymentId,
                name = occ.Name,
                amount = ObfuscationHelper.Obfuscate(scheduledAmt),
                dueDate = occ.OccurrenceDate.ToString("yyyy-MM-dd"),
                dueDay = occ.OccurrenceDate.Day,
                offsetDays,
                accountId,
                accountName = account?.Name ?? "Account",
                accountBalance = ObfuscationHelper.Obfuscate(accountBal),
                shortfall = ObfuscationHelper.Obfuscate(shortfall)
            }));
        }

        return shortfalls.OrderBy(item => item.OffsetDays).Select(item => item.Shortfall).ToList();
    }
}
