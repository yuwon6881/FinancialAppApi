namespace FinancialAppApi.Services;

/// <summary>
/// How a recurring occurrence's ledger account is resolved for settlement.
/// </summary>
public static class RecurringOccurrenceAccounts
{
    /// <summary>
    /// The account an occurrence was scheduled against, or <c>null</c> when it was never assigned
    /// one.
    ///
    /// The occurrence's own snapshot wins over the schedule's current account, so re-pointing a
    /// bill moves its future occurrences rather than one already waiting to be settled. A blank
    /// value means "never assigned", not "assigned to nothing": bills authored before account
    /// attribution carry an empty <see cref="Models.RecurringPayment.AccountId"/>, and that empty
    /// value propagates onto every occurrence they materialise. Callers must treat null as "no
    /// identity to preserve" and fall back to validating the bucket alone -- demanding identity
    /// with a blank made those settlements permanently unsyncable, because the refusal asked for
    /// an account and then rejected every account the client could offer.
    /// </summary>
    public static string? Scheduled(string? occurrenceAccountId, string? paymentAccountId)
        => !string.IsNullOrWhiteSpace(occurrenceAccountId) ? occurrenceAccountId.Trim()
            : !string.IsNullOrWhiteSpace(paymentAccountId) ? paymentAccountId.Trim()
            : null;
}
