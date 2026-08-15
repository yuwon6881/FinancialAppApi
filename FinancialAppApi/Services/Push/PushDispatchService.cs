using System.Globalization;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Accounts;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Push;

public sealed record PushDispatchSummary(int Sent, int Skipped, int Disabled);

// The daily fan-out job: for every user with push reminders enabled who has at least one
// enabled device subscription, finds each of their recurring payments' next due (unpaid)
// occurrence and, if it falls inside that payment's configured lead window (or is an upcoming
// auto-deduct bill with insufficient account balance 1 day prior), sends exactly one
// reminder per device — using an insert-before-send claim row so retries/concurrent runs can
// never double-send. Runs one user at a time in its own DbContext scope (mirroring
// ReceiptScanProcessor) so per-user tenancy invariants on AppDbContext are respected even though
// this job itself spans every account.
public partial class PushDispatchService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FinancialClock _financialClock;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PushDispatchService> _logger;

    public PushDispatchService(
        IServiceScopeFactory scopeFactory,
        FinancialClock financialClock,
        IConfiguration configuration,
        ILogger<PushDispatchService> logger)
    {
        _scopeFactory = scopeFactory;
        _financialClock = financialClock;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<PushDispatchSummary> DispatchAsync(CancellationToken cancellationToken = default)
    {
        // Fails closed before claiming a single reminder: every send would fail anyway without a
        // configured FCM project, and a claim row is written BEFORE the send is attempted, so
        // running with this missing would permanently burn today's claim on a guaranteed failure.
        if (string.IsNullOrWhiteSpace(_configuration["Fcm:ProjectId"]))
        {
            _logger.LogWarning("Push dispatch skipped: Fcm:ProjectId is not configured.");
            return new PushDispatchSummary(0, 0, 0);
        }

        var today = _financialClock.Today;
        var localNow = _financialClock.LocalNow;

        Dictionary<string, DispatchCandidateUser> candidates;
        using (var loadScope = _scopeFactory.CreateScope())
        {
            var loadContext = loadScope.ServiceProvider.GetRequiredService<AppDbContext>();
            candidates = await LoadCandidatesAsync(loadContext, cancellationToken);
        }

        int sent = 0, skipped = 0, disabled = 0;

        foreach (var (userId, candidate) in candidates)
        {
            try
            {
                using var userScope = _scopeFactory.CreateScope();
                var context = userScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var occurrenceLedger = userScope.ServiceProvider.GetRequiredService<RecurringOccurrenceLedgerService>();
                var accountService = userScope.ServiceProvider.GetRequiredService<LedgerAccountService>();
                var fcmSender = userScope.ServiceProvider.GetRequiredService<IFcmPushSender>();
                context.SetCurrentUser(userId);

                // Subscription gating: no device opted into *bill reminders*, nothing to send for
                // this account at all. A device that only asked for spending alerts is enabled
                // but is deliberately not in this fan-out.
                var subscriptions = await context.PushSubscriptions
                    .Where(s => s.Enabled && s.BillRemindersEnabled)
                    .ToListAsync(cancellationToken);
                if (subscriptions.Count == 0)
                {
                    continue;
                }

                var userAccounts = await accountService.GetAccountsAsync(cancellationToken);
                var userBalances = await accountService.GetBalancesAsync(userAccounts, cancellationToken);
                var userAccountsById = userAccounts.ToDictionary(a => a.Id, StringComparer.Ordinal);

                foreach (var payment in candidate.Payments)
                {
                    var due = await ResolveDueOccurrenceAsync(occurrenceLedger, payment, today, cancellationToken);
                    if (due == null)
                    {
                        continue;
                    }

                    var occurrenceDate = due.OccurrenceDate;
                    var offsetDays = occurrenceDate.DayNumber - today.DayNumber;
                    var isDaily = string.Equals(payment.PushReminderMode, "Daily", StringComparison.OrdinalIgnoreCase);
                    var isAutoDeduct = payment.PaymentMode == RecurringPaymentMode.AutoDeduct;

                    // The occurrence snapshot is authoritative for both the amount and the account,
                    // exactly as settlement reads them: re-pointing or re-pricing a bill must not
                    // change what an already-materialised occurrence will actually deduct. Math.Abs
                    // because the sign of a stored recurring amount is not a contract — every other
                    // reader defends against it the same way.
                    var accountId = string.IsNullOrWhiteSpace(due.AccountId) ? payment.AccountId : due.AccountId;
                    var account = string.IsNullOrWhiteSpace(accountId)
                        ? null
                        : userAccountsById.GetValueOrDefault(accountId);
                    var scheduledAmount = Math.Abs(due.ScheduledAmount ?? payment.Amount);
                    // Without a resolvable account there is no balance to compare against. Treating
                    // a missing one as a zero balance reported a full-amount shortfall against an
                    // account that does not exist, for every legacy bill carrying no placement.
                    var accountBalance = account is null ? 0m : userBalances.GetValueOrDefault(account.Id);
                    var isShortfallOneDayPrior = isAutoDeduct
                        && offsetDays == 1
                        && account is not null
                        && accountBalance < scheduledAmount;

                    var shouldSendStandard = payment.PushReminderEnabled && (isDaily
                        ? offsetDays <= payment.PushReminderLeadDays
                        : offsetDays == payment.PushReminderLeadDays);

                    if (!isShortfallOneDayPrior && !shouldSendStandard)
                    {
                        continue;
                    }

                    decimal? shortfallAmount = isShortfallOneDayPrior ? (scheduledAmount - accountBalance) : null;
                    string? accountName = isShortfallOneDayPrior ? account?.Name : null;

                    foreach (var subscription in subscriptions)
                    {
                        // One device's failure (bad token, unexpected sender exception, etc.) must
                        // never take down the rest of this user's devices or any other user's
                        // reminders in the same run.
                        try
                        {
                            var (outcome, sentIncrement, skippedIncrement, disabledIncrement) = await TrySendOneAsync(
                                context,
                                fcmSender,
                                payment,
                                occurrenceDate,
                                offsetDays,
                                isDaily,
                                shouldSendStandard,
                                subscription,
                                today,
                                localNow,
                                shortfallAmount,
                                accountName,
                                cancellationToken);

                            sent += sentIncrement;
                            skipped += skippedIncrement;
                            disabled += disabledIncrement;
                            _ = outcome;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            skipped++;
                            _logger.LogWarning(
                                ex,
                                "Push reminder send threw unexpectedly for recurring payment {RecurringPaymentId}; isolated to this device.",
                                payment.Id);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never let one account's unexpected failure abort the whole run — every other
                // account still gets its own chance at its reminders.
                _logger.LogWarning(ex, "Push dispatch failed for one account; continuing with the rest.");
            }
        }

        var categorySummary = await DispatchPendingCategoryAlertsAsync(cancellationToken);
        return new PushDispatchSummary(
            sent + categorySummary.Sent,
            skipped + categorySummary.Skipped,
            disabled + categorySummary.Disabled);
    }

    private async Task<(string Outcome, int Sent, int Skipped, int Disabled)> TrySendOneAsync(
        AppDbContext context,
        IFcmPushSender fcmSender,
        RecurringPayment payment,
        DateOnly occurrenceDate,
        int offsetDays,
        bool isDaily,
        bool sendStandard,
        PushSubscription subscription,
        DateOnly today,
        DateTime localNow,
        decimal? shortfallAmount,
        string? accountName,
        CancellationToken cancellationToken)
    {
        var isShortfall = shortfallAmount.HasValue && shortfallAmount.Value > 0;

        // A single send can discharge both claims: a shortfall alert going out on the same day the
        // configured reminder is due already says everything that reminder would have. So it claims
        // every kind it covers, and the send is skipped only when nothing is left unclaimed —
        // otherwise a shortfall claimed at offset 1 would satisfy the offset-agnostic Once lookup
        // and silently swallow the reminder the user actually asked for.
        var kinds = new List<string>(2);
        if (isShortfall) kinds.Add(PushReminderDeliveryKind.Shortfall);
        if (sendStandard) kinds.Add(PushReminderDeliveryKind.Reminder);

        var unclaimed = new List<string>(kinds.Count);
        foreach (var kind in kinds)
        {
            // Once sends a single catch-up reminder anywhere inside the lead window, so "already
            // sent" ignores the offset it was originally claimed at. Countdown sends once per day,
            // and a shortfall alert is about one specific day, so both keep the offset in the key
            // and no backfill ever happens for a missed day.
            var offsetScoped = isDaily || kind == PushReminderDeliveryKind.Shortfall;
            var claimed = offsetScoped
                ? await context.PushReminderDeliveries.AnyAsync(d =>
                    d.RecurringPaymentId == payment.Id &&
                    d.OccurrenceDate == occurrenceDate &&
                    d.ActualOffsetDays == offsetDays &&
                    d.SubscriptionId == subscription.Id &&
                    d.Kind == kind, cancellationToken)
                : await context.PushReminderDeliveries.AnyAsync(d =>
                    d.RecurringPaymentId == payment.Id &&
                    d.OccurrenceDate == occurrenceDate &&
                    d.SubscriptionId == subscription.Id &&
                    d.Kind == kind, cancellationToken);
            if (!claimed)
            {
                unclaimed.Add(kind);
            }
        }

        if (unclaimed.Count == 0)
        {
            return ("AlreadySent", 0, 1, 0);
        }

        var claims = unclaimed
            .Select(kind => new PushReminderDelivery
            {
                Id = $"prd-{Guid.NewGuid():N}",
                RecurringPaymentId = payment.Id,
                OccurrenceDate = occurrenceDate,
                ActualOffsetDays = offsetDays,
                SubscriptionId = subscription.Id,
                Kind = kind,
                SentAt = DateTime.UtcNow
            })
            .ToList();
        context.PushReminderDeliveries.AddRange(claims);

        try
        {
            // The claim is committed BEFORE the FCM call is ever attempted: if this process (or
            // a same-day retry) reaches this reminder again, the unique index above already
            // reflects the claim, so it can never be sent twice even if FCM itself later fails.
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            foreach (var claim in claims)
            {
                context.Entry(claim).State = EntityState.Detached;
            }
            return ("RaceLost", 0, 1, 0);
        }

        var content = BuildContent(payment, occurrenceDate, offsetDays, today, localNow, shortfallAmount, accountName);
        var result = await fcmSender.SendAsync(subscription.FcmToken, content, cancellationToken);

        switch (result.Status)
        {
            case FcmSendStatus.Sent:
                return ("Sent", 1, 0, 0);
            case FcmSendStatus.InvalidOrUnregistered:
                // FCM has retired this token, so the device is off for every kind at once
                // regardless of what its user asked for — keeping a channel flag on would leave
                // the Enabled == (bills || alerts) invariant broken and the switch showing "on".
                subscription.Enabled = false;
                subscription.BillRemindersEnabled = false;
                subscription.CategoryAlertsEnabled = false;
                subscription.FcmToken = string.Empty;
                await context.SaveChangesAsync(cancellationToken);
                return ("Disabled", 0, 0, 1);
            default:
                // Never logs the token or any payment amount — just that a send failed. The
                // claim above already stands, so a same-day retry will not resend this one.
                context.PushReminderDeliveries.RemoveRange(claims);
                await context.SaveChangesAsync(cancellationToken);
                _logger.LogWarning(
                    "Push reminder send failed for recurring payment {RecurringPaymentId} occurrence {OccurrenceDate}.",
                    payment.Id,
                    occurrenceDate);
                return ("TransientFailure", 0, 1, 0);
        }
    }

    private static PushNotificationContent BuildContent(
        RecurringPayment payment,
        DateOnly occurrenceDate,
        int offsetDays,
        DateOnly today,
        DateTime localNow,
        decimal? shortfall = null,
        string? accountName = null)
    {
        string body;
        var data = new Dictionary<string, string>
        {
            ["recurringPaymentId"] = payment.Id,
            ["occurrenceDate"] = occurrenceDate.ToString("yyyy-MM-dd")
        };

        if (shortfall.HasValue && shortfall.Value > 0)
        {
            body = offsetDays switch
            {
                0 => $"Auto-deducts today: needs {shortfall.Value:N2} more in {accountName ?? "account"}",
                1 => $"Auto-deducts tomorrow: needs {shortfall.Value:N2} more in {accountName ?? "account"}",
                _ => $"Auto-deducts in {offsetDays} days: needs {shortfall.Value:N2} more in {accountName ?? "account"}"
            };
            data["shortfall"] = shortfall.Value.ToString("F2", CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(accountName))
            {
                data["accountName"] = accountName;
            }
        }
        else
        {
            body = offsetDays switch
            {
                0 => "Due today",
                1 => "Due tomorrow",
                _ => $"Due in {offsetDays} days"
            };
        }

        // Short TTL: the reminder should never survive past the end of the Malaysia calendar day
        // it was generated for, so a delayed delivery never arrives as a stale/wrong-day message.
        var endOfDay = today.ToDateTime(TimeOnly.MinValue).AddDays(1);
        var timeToLive = endOfDay - localNow;
        if (timeToLive < TimeSpan.FromMinutes(1))
        {
            timeToLive = TimeSpan.FromMinutes(1);
        }

        return new PushNotificationContent(
            Kind: "recurring-payment",
            Title: payment.Name,
            Body: body,
            // Stable across resends for the same occurrence so the OS collapses/replaces the
            // notification instead of stacking a new one for every countdown day.
            Tag: $"payment:{payment.Id}:{occurrenceDate:yyyy-MM-dd}",
            Route: $"/recurring?subscription={Uri.EscapeDataString(payment.Id)}",
            TimeToLive: timeToLive,
            Data: data);
    }

    // Returns the occurrence itself, not just its date: its snapshotted amount and account are
    // what settlement will actually use, so a shortfall must be measured against those.
    private static async Task<RecurringPaymentOccurrence?> ResolveDueOccurrenceAsync(
        RecurringOccurrenceLedgerService occurrenceLedger,
        RecurringPayment payment,
        DateOnly today,
        CancellationToken cancellationToken) =>
        await occurrenceLedger.GetNextPendingAsync(payment, today, includeFrom: true, cancellationToken);

    private async Task<Dictionary<string, DispatchCandidateUser>> LoadCandidatesAsync(
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        // This job spans every account, so it must bypass the per-request tenant query filter
        // (there is no "current user" yet) purely to discover who is opted in.
        var subscribedUserIds = await context.PushSubscriptions
            .IgnoreQueryFilters()
            .Where(s => s.Enabled && s.BillRemindersEnabled)
            .Select(s => s.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (subscribedUserIds.Count == 0)
        {
            return new Dictionary<string, DispatchCandidateUser>();
        }

        // Enabled device subscriptions, rather than the legacy account flag, are authoritative.
        // This preserves delivery to mobile when an older desktop client previously cleared the
        // account flag and left the mobile subscription intact.
        var settings = await context.FinancialSettings
            .IgnoreQueryFilters()
            .Where(s => subscribedUserIds.Contains(s.UserId))
            .Select(s => new { s.UserId, s.CycleDay })
            .ToListAsync(cancellationToken);
        if (settings.Count == 0)
        {
            return new Dictionary<string, DispatchCandidateUser>();
        }

        var enabledUserIds = settings.Select(s => s.UserId).ToHashSet();
        var payments = await context.RecurringPayments
            .IgnoreQueryFilters()
            .Where(p => p.Active && (p.PushReminderEnabled || p.PaymentMode == RecurringPaymentMode.AutoDeduct) && enabledUserIds.Contains(p.UserId))
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, DispatchCandidateUser>();
        foreach (var setting in settings)
        {
            var userPayments = payments.Where(p => p.UserId == setting.UserId).ToList();
            if (userPayments.Count == 0)
            {
                continue;
            }

            result[setting.UserId] = new DispatchCandidateUser(setting.CycleDay, userPayments);
        }

        return result;
    }

    private sealed record DispatchCandidateUser(int CycleDay, List<RecurringPayment> Payments);
}
