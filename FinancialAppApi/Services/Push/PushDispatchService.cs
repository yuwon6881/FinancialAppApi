using System.Globalization;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Accounts;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Push;

// Failed counts sends a retry could still rescue (network, 5xx, credential trouble) and is
// deliberately separate from Skipped, which is dominated by the benign "already claimed today"
// case. Configured is false only when the run could not begin at all. Cloud Scheduler can only see
// the status code, so any retryable failure must make the endpoint non-2xx even when other devices
// succeeded. Successful devices keep their claims; only unfinished sends are attempted again.
public sealed record PushDispatchSummary(int Sent, int Skipped, int Disabled, int Failed = 0, bool Configured = true)
{
    public bool RequiresRetry => !Configured || Failed > 0;
}

// The daily fan-out job: for every user with push reminders enabled who has at least one enabled
// device subscription, finds each of their recurring payments' next due (unpaid) occurrence and, if
// it falls inside that payment's configured lead window (or is an upcoming auto-deduct bill with
// insufficient account balance 1 day prior), sends exactly one reminder per device — using an
// insert-before-send claim row so retries and concurrent runs can never double-send. Runs one user
// at a time in its own DbContext scope (mirroring ReceiptScanProcessor) so per-user tenancy
// invariants on AppDbContext are respected even though this job itself spans every account.
public sealed partial class PushDispatchService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PushDispatchService> _logger;

    public PushDispatchService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<PushDispatchService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<PushDispatchSummary> DispatchAsync(
        CancellationToken cancellationToken = default) =>
        await DispatchDueRemindersAsync(cancellationToken);

    public async Task<PushDispatchSummary> DispatchDueRemindersAsync(
        CancellationToken cancellationToken = default)
    {
        // Fails closed before claiming a single reminder: every send would fail anyway without a
        // configured FCM project, and a claim row is written BEFORE the send is attempted. Running
        // without this walks the whole fan-out and logs a failure per device per user for something
        // known up front, and any future send path that does not clean up its claim on failure would
        // permanently burn the day's reminder.
        if (string.IsNullOrWhiteSpace(_configuration["Fcm:ProjectId"]))
        {
            _logger.LogWarning("Push dispatch skipped: Fcm:ProjectId is not configured.");
            return new PushDispatchSummary(0, 0, 0, Failed: 0, Configured: false);
        }

        var sent = 0;
        var skipped = 0;
        var disabled = 0;
        var failed = 0;

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var fcmSender = scope.ServiceProvider.GetRequiredService<IFcmPushSender>();
        var clock = scope.ServiceProvider.GetRequiredService<FinancialClock>();
        var today = clock.Today;
        var localNow = clock.LocalNow;

        var candidates = await LoadCandidatesAsync(context, cancellationToken);
        if (candidates.Count == 0)
        {
            var categoryInitialSummary = await DispatchPendingCategoryAlertsAsync(cancellationToken);
            return new PushDispatchSummary(
                sent + categoryInitialSummary.Sent,
                skipped + categoryInitialSummary.Skipped,
                disabled + categoryInitialSummary.Disabled,
                failed + categoryInitialSummary.Failed);
        }

        foreach (var (userId, candidate) in candidates)
        {
            try
            {
                using var userScope = _scopeFactory.CreateScope();
                var userContext = userScope.ServiceProvider.GetRequiredService<AppDbContext>();
                userContext.SetCurrentUser(userId);
                var userOccurrenceLedger = userScope.ServiceProvider.GetRequiredService<RecurringOccurrenceLedgerService>();
                var userAccountBalanceService = userScope.ServiceProvider.GetRequiredService<LedgerAccountBalanceService>();
                var userSubscriptionService = userScope.ServiceProvider.GetRequiredService<PushSubscriptionService>();

                var subscriptions = await userContext.PushSubscriptions
                    .Where(s => s.Enabled && s.BillRemindersEnabled)
                    .ToListAsync(cancellationToken);
                if (subscriptions.Count == 0)
                {
                    continue;
                }

                var userAccounts = await userContext.LedgerAccounts
                    .ToListAsync(cancellationToken);
                var userAccountsById = userAccounts.ToDictionary(account => account.Id, StringComparer.Ordinal);
                var userBalances = await userAccountBalanceService.GetBalancesAsync(userAccounts, cancellationToken);

                var duePayments = new List<(RecurringPayment Payment, RecurringPaymentOccurrence Due)>();
                foreach (var payment in candidate.Payments)
                {
                    var due = await ResolveDueOccurrenceAsync(userOccurrenceLedger, payment, today, cancellationToken);
                    if (due == null)
                    {
                        continue;
                    }

                    duePayments.Add((payment, due));
                }

                // Only a partially paid occurrence needs its ledger rows read; every other status
                // either owes its full scheduled amount or owes nothing.
                var partiallyPaidDue = duePayments
                    .Where(item => item.Due.Status == RecurringOccurrenceStatus.PartiallyPaid)
                    .Select(item => item.Due)
                    .ToList();
                var partialPaidByOccurrence = RecurringOccurrenceAmounts.PaidByOccurrence(
                    partiallyPaidDue.Count == 0
                        ? []
                        : await LoadOccurrenceTransactionsAsync(userContext, partiallyPaidDue, cancellationToken));

                var projectedDebits = duePayments
                    .Where(item => item.Payment.PaymentMode == RecurringPaymentMode.AutoDeduct)
                    .Select(item =>
                    {
                        var accountId = string.IsNullOrWhiteSpace(item.Due.AccountId)
                            ? item.Payment.AccountId
                            : item.Due.AccountId;
                        var account = string.IsNullOrWhiteSpace(accountId)
                            ? null
                            : userAccountsById.GetValueOrDefault(accountId);
                        return new
                        {
                            item.Due,
                            AccountId = accountId,
                            Account = account,
                            Amount = RecurringOccurrenceAmounts.Outstanding(item.Due, item.Payment.Amount, partialPaidByOccurrence),
                            OffsetDays = item.Due.OccurrenceDate.DayNumber - today.DayNumber
                        };
                    })
                    .Where(item => item.Account is not null && !string.IsNullOrWhiteSpace(item.AccountId)
                        && item.OffsetDays is >= 0 and <= 31 && item.Amount > 0m)
                    .Select(item => new RecurringAccountDebit(
                        item.Due.Id,
                        item.AccountId!,
                        item.Due.OccurrenceDate,
                        item.Amount));
                var projections = RecurringAccountBalanceProjection.Project(projectedDebits, userBalances);

                foreach (var (payment, due) in duePayments)
                {
                    var occurrenceDate = due.OccurrenceDate;
                    var offsetDays = occurrenceDate.DayNumber - today.DayNumber;
                    var isDaily = string.Equals(payment.PushReminderMode, "Daily", StringComparison.OrdinalIgnoreCase);
                    var isAutoDeduct = payment.PaymentMode == RecurringPaymentMode.AutoDeduct;

                    // The occurrence snapshot is authoritative for both the amount and the account,
                    // exactly as settlement reads them: re-pointing or re-pricing a bill must not
                    // change what an already-materialised occurrence will actually deduct.
                    var accountId = string.IsNullOrWhiteSpace(due.AccountId) ? payment.AccountId : due.AccountId;
                    var account = string.IsNullOrWhiteSpace(accountId)
                        ? null
                        : userAccountsById.GetValueOrDefault(accountId);
                    var scheduledAmount = RecurringOccurrenceAmounts.Outstanding(due, payment.Amount, partialPaidByOccurrence);
                    projections.TryGetValue(due.Id, out var projection);
                    var isShortfallOneDayPrior = isAutoDeduct
                        && offsetDays == 1
                        && account is not null
                        && projection is not null
                        && projection.Shortfall > 0m;

                    var shouldSendStandard = payment.PushReminderEnabled && (isDaily
                        ? offsetDays <= payment.PushReminderLeadDays
                        : offsetDays == payment.PushReminderLeadDays);

                    if (!isShortfallOneDayPrior && !shouldSendStandard)
                    {
                        continue;
                    }

                    decimal? shortfallAmount = isShortfallOneDayPrior ? projection!.Shortfall : null;
                    string? accountName = isShortfallOneDayPrior ? account?.Name : null;

                    foreach (var subscription in subscriptions)
                    {
                        // One device's failure (bad token, unexpected sender exception) must never
                        // take down the rest of this user's devices or any other user's reminders.
                        try
                        {
                            var (outcome, sentIncrement, skippedIncrement, disabledIncrement, failedIncrement) = await TrySendOneAsync(
                                userContext,
                                fcmSender,
                                userSubscriptionService,
                                payment,
                                due,
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
                            failed += failedIncrement;
                            _ = outcome;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            skipped++;
                            failed++;
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
                failed++;
                _logger.LogWarning(ex, "Push dispatch failed for one account; continuing with the rest.");
            }
        }

        var categorySummary = await DispatchPendingCategoryAlertsAsync(cancellationToken);
        return new PushDispatchSummary(
            sent + categorySummary.Sent,
            skipped + categorySummary.Skipped,
            disabled + categorySummary.Disabled,
            failed + categorySummary.Failed);
    }

    private async Task<(string Outcome, int Sent, int Skipped, int Disabled, int Failed)> TrySendOneAsync(
        AppDbContext context,
        IFcmPushSender fcmSender,
        PushSubscriptionService subscriptionService,
        RecurringPayment payment,
        RecurringPaymentOccurrence due,
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
        // otherwise a shortfall claimed at offset 1 would satisfy the offset-agnostic Once lookup and
        // silently swallow the reminder the user actually asked for.
        var kinds = new List<string>(2);
        if (isShortfall) kinds.Add(PushReminderDeliveryKind.Shortfall);
        if (sendStandard) kinds.Add(PushReminderDeliveryKind.Reminder);

        var unclaimed = new List<string>(kinds.Count);
        foreach (var kind in kinds)
        {
            // Once fires on exactly one offset (see shouldSendStandard), so its "already sent"
            // lookup deliberately ignores the offset it was claimed at: the case that matters is
            // PushReminderLeadDays being edited mid-window, which would otherwise present the same
            // occurrence at a second offset and send a duplicate. Daily sends once per day, and a
            // shortfall alert is about one specific day, so both keep the offset in the key and no
            // backfill ever happens for a missed day.
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
            return ("AlreadySent", 0, 1, 0, 0);
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
            // The claim is committed BEFORE the FCM call is ever attempted: if this process (or a
            // same-day retry) reaches this reminder again, the unique index already reflects the
            // claim, so it can never be sent twice even if FCM itself later fails.
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            foreach (var claim in claims)
            {
                context.Entry(claim).State = EntityState.Detached;
            }
            return ("RaceLost", 0, 1, 0, 0);
        }

        var isPartiallyPaid = due.Status == RecurringOccurrenceStatus.PartiallyPaid;
        var content = BuildContent(
            payment,
            occurrenceDate,
            offsetDays,
            today,
            localNow,
            shortfallAmount,
            accountName,
            isPartiallyPaid) with { Platform = PushPlatform.Normalize(subscription.Platform) };
        var result = await fcmSender.SendAsync(subscription.FcmToken, content, cancellationToken);

        switch (result.Status)
        {
            case FcmSendStatus.Sent:
                return ("Sent", 1, 0, 0, 0);
            case FcmSendStatus.InvalidOrUnregistered:
                // This send is known not to have reached the device. Release the claims before
                // retiring the token so a renewed registration can receive the reminder later.
                context.PushReminderDeliveries.RemoveRange(claims);
                await subscriptionService.RetireInvalidTokenAsync(subscription, cancellationToken);
                return ("Disabled", 0, 0, 1, 0);
            default:
                // Never logs the token or any payment amount — just that a send failed. The claim
                // above is removed, so a same-day retry may send this reminder again.
                context.PushReminderDeliveries.RemoveRange(claims);
                await context.SaveChangesAsync(cancellationToken);
                _logger.LogWarning(
                    "Push reminder send failed for recurring payment {RecurringPaymentId} occurrence {OccurrenceDate}.",
                    payment.Id,
                    occurrenceDate);
                return ("TransientFailure", 0, 1, 0, 1);
        }
    }

    private static PushNotificationContent BuildContent(
        RecurringPayment payment,
        DateOnly occurrenceDate,
        int offsetDays,
        DateOnly today,
        DateTime localNow,
        decimal? shortfall = null,
        string? accountName = null,
        bool isPartiallyPaid = false)
    {
        string body = "Open FinancialApp to review.";
        var data = new Dictionary<string, string>
        {
            ["recurringPaymentId"] = payment.Id,
            ["occurrenceDate"] = occurrenceDate.ToString("yyyy-MM-dd")
        };

        if (shortfall.HasValue && shortfall.Value > 0)
        {
            // One arm, deliberately: a shortfall alert is only ever raised at offsetDays == 1 (see
            // isShortfallOneDayPrior), so same-day and multi-day wordings were unreachable copy.
            body = $"Auto-deducts tomorrow: needs {shortfall.Value:N2} more in {accountName ?? "account"}";
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
                // No overdue wording, deliberately: ResolveDueOccurrenceAsync resolves from today
                // forward, so offsetDays is never negative and an overdue arm would be dead copy.
                // Reminding about a bill that is already late means teaching that resolver to look
                // backwards first, with a bound on how far, or Daily mode would re-notify every day
                // for as long as the row stays open.
                0 => isPartiallyPaid ? "Part paid, balance due today" : "Due today",
                1 => isPartiallyPaid ? "Part paid, balance due tomorrow" : "Due tomorrow",
                _ => isPartiallyPaid ? $"Part paid, balance due in {offsetDays} days" : $"Due in {offsetDays} days"
            };
        }

        // Short TTL: the reminder should never survive past the end of the Malaysia calendar day it
        // was generated for, so a delayed delivery never arrives as a stale, wrong-day message.
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
            // Must equal the client's buildNotificationTag() for this payload (PUSH-03). The worker
            // recomputes it from `data` and ignores whatever arrives here, so a different value was
            // never wrong on screen -- it just described a tagging scheme nothing implements.
            Tag: $"recurring-reminder-{payment.Id}-{occurrenceDate:yyyy-MM-dd}",
            Route: $"/recurring?subscription={Uri.EscapeDataString(payment.Id)}",
            TimeToLive: timeToLive,
            Data: data);
    }

    private static async Task<List<Transaction>> LoadOccurrenceTransactionsAsync(
        AppDbContext context,
        IReadOnlyCollection<RecurringPaymentOccurrence> occurrences,
        CancellationToken cancellationToken)
    {
        var paymentIds = occurrences.Select(o => o.RecurringPaymentId).Distinct(StringComparer.Ordinal).ToList();
        var dates = occurrences.Select(o => o.OccurrenceDate).Distinct().ToList();
        return await context.Transactions
            .AsNoTracking()
            .Where(t => t.RecurringPaymentId != null
                && paymentIds.Contains(t.RecurringPaymentId)
                && t.RecurringOccurrenceDate != null
                && dates.Contains(t.RecurringOccurrenceDate.Value))
            .ToListAsync(cancellationToken);
    }

    // Returns the occurrence itself, not just its date: its snapshotted amount and account are what
    // settlement will actually use, so a shortfall must be measured against those. It resolves from
    // today forward, which is why no caller ever sees a negative offset.
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
        // This job spans every account, so it must bypass the per-request tenant query filter (there
        // is no "current user" yet) purely to discover who is opted in.
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

        // Enabled device subscriptions, rather than the legacy account flag, are authoritative. This
        // preserves delivery to mobile when an older desktop client previously cleared the account
        // flag and left the mobile subscription intact. FinancialSettings is not part of the
        // subscription contract; a user with a missing settings row must still receive push.
        var payments = await context.RecurringPayments
            .IgnoreQueryFilters()
            .Where(p => p.Active && (p.PushReminderEnabled || p.PaymentMode == RecurringPaymentMode.AutoDeduct) && subscribedUserIds.Contains(p.UserId))
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, DispatchCandidateUser>();
        foreach (var userId in subscribedUserIds)
        {
            var userPayments = payments.Where(p => p.UserId == userId).ToList();
            if (userPayments.Count == 0)
            {
                continue;
            }

            result[userId] = new DispatchCandidateUser(userPayments);
        }

        return result;
    }

    private sealed record DispatchCandidateUser(List<RecurringPayment> Payments);
}
