using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Models;

namespace FinancialAppApi.Database;

public partial class AppDbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Order is preserved from the single method these calls replaced: entity
        // configuration first, then the tenancy filters that build on it.
        ConfigureLedgerModel(modelBuilder);
        ConfigureIdentityModel(modelBuilder);
        ConfigureCommitmentModel(modelBuilder);
        ConfigureCycleModel(modelBuilder);
        ConfigureInvestmentModel(modelBuilder);
        ConfigureVaultAndAiModel(modelBuilder);
        ConfigureTenancy(modelBuilder);
    }

    /// <summary>Ledger rows, per-bucket accounts, recurring bills and the settings row.</summary>
    private void ConfigureLedgerModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Transaction>(entity =>
        {
            // Bounded numeric(12,2) instead of unbounded numeric: exact for money, but
            // fixed/smaller on-disk, which matters against the 500MB free storage ceiling.
            entity.Property(e => e.Amount).HasColumnType("numeric(12,2)");
            entity.Property(e => e.ExcludeFromAutocomplete).HasDefaultValue(false);
            entity.Property(e => e.IsAccountBalanceAdjustment).HasDefaultValue(false);
            entity.Property(e => e.StabilityRecoveryTopUpAmount).HasColumnType("numeric(12,2)");
            entity.Property(e => e.Date).HasColumnType("timestamp with time zone");
            entity.Property(e => e.PostedAt).HasColumnType("timestamp with time zone");
            // Calendar date is primary; PostedAt resolves the order of records on that date.
            entity.HasIndex(e => new { e.UserId, e.Date, e.PostedAt, e.LedgerCategory })
                .IsDescending(false, true, true, false);
            entity.Property(e => e.StabilityReloadIntent)
                .HasDefaultValue(StabilityReloadIntent.Unanswered);
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_transactions_stabilityreloadintent",
                "\"StabilityReloadIntent\" IN ('Unanswered', 'Required', 'NotRequired')"));
            entity.HasIndex(e => new { e.UserId, e.WishlistItemId })
                .IsUnique()
                .HasFilter("\"WishlistItemId\" IS NOT NULL");
            entity.HasIndex(e => new { e.UserId, e.SavingsGoalId });
            entity.HasIndex(e => new { e.UserId, e.AccountId });
            entity.HasOne<LedgerAccount>()
                .WithMany()
                .HasForeignKey(e => new { e.UserId, e.AccountId })
                .HasPrincipalKey(e => new { e.UserId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LedgerAccount>()
                .WithMany()
                .HasForeignKey(e => new { e.UserId, e.CounterAccountId })
                .HasPrincipalKey(e => new { e.UserId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_transactions_account_tracking",
                "(lower(\"LedgerCategory\") IN ('essentials', 'growth', 'stability', 'rewards') AND \"AccountId\" IS NOT NULL AND \"CounterAccountId\" IS NULL) OR (lower(\"LedgerCategory\") = 'accountmove' AND \"AccountId\" IS NOT NULL AND \"CounterAccountId\" IS NOT NULL) OR (lower(\"LedgerCategory\") LIKE 'transfer:%' AND ((lower(\"LedgerCategory\") LIKE 'transfer:income->%' AND \"AccountId\" IS NOT NULL AND \"CounterAccountId\" IS NULL) OR (lower(\"LedgerCategory\") NOT LIKE 'transfer:income->%' AND \"AccountId\" IS NOT NULL AND \"CounterAccountId\" IS NOT NULL))) OR (lower(\"LedgerCategory\") NOT IN ('essentials', 'growth', 'stability', 'rewards', 'accountmove') AND lower(\"LedgerCategory\") NOT LIKE 'transfer:%' AND \"AccountId\" IS NULL AND \"CounterAccountId\" IS NULL)"));
            // Non-unique index for multiple partial recurring-payment occurrence contributions.
            entity.HasIndex(e => new { e.UserId, e.RecurringPaymentId, e.RecurringOccurrenceDate })
                .HasFilter("\"RecurringPaymentId\" IS NOT NULL AND \"RecurringOccurrenceDate\" IS NOT NULL");
        });

        modelBuilder.Entity<LedgerAccount>(entity =>
        {
            entity.Property(e => e.Id).HasMaxLength(100);
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.Bucket).HasMaxLength(20);
            entity.Property(e => e.Kind).HasMaxLength(20).HasDefaultValue(LedgerAccountKind.Bank);
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(e => new { e.UserId, e.Name }).IsUnique();
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "ck_ledgeraccounts_bucket",
                    $"\"Bucket\" IN ({string.Join(", ", FinancialConstants.BudgetCategories.Select(value => $"'{value}'"))})");
                table.HasCheckConstraint(
                    "ck_ledgeraccounts_kind",
                    $"\"Kind\" IN ({string.Join(", ", LedgerAccountKind.Values.Select(value => $"'{value}'"))})");
            });
            entity.HasAlternateKey(e => new { e.UserId, e.Id });
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasIndex(e => e.RegistrationSlot).IsUnique();
            entity.HasIndex(e => e.NormalizedUsername).IsUnique();
        });

        modelBuilder.Entity<RecurringPayment>(entity =>
        {
            entity.Property(e => e.Amount).HasColumnType("numeric(12,2)");
            entity.Property(e => e.PushReminderMode).HasDefaultValue("Once");
            entity.Property(e => e.PushReminderLeadDays).HasDefaultValue(1);
            entity.Property(e => e.PaymentMode).HasDefaultValue(RecurringPaymentMode.Manual);
            entity.Property(e => e.AccountId).HasMaxLength(100);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.UserId, e.AccountId });
            entity.HasOne<LedgerAccount>()
                .WithMany()
                .HasForeignKey(e => new { e.UserId, e.AccountId })
                .HasPrincipalKey(e => new { e.UserId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("ck_recurringpayments_amount_nonzero", "\"Amount\" <> 0");
                t.HasCheckConstraint("ck_recurringpayments_pushremindermode", "\"PushReminderMode\" IN ('Once', 'Daily')");
                t.HasCheckConstraint("ck_recurringpayments_pushreminderleaddays", "\"PushReminderLeadDays\" IN (1, 2, 3, 7)");
                t.HasCheckConstraint("ck_recurringpayments_paymentmode", "\"PaymentMode\" IN ('AutoDeduct', 'Manual')");
            });
        });

        modelBuilder.Entity<RecurringPaymentOccurrence>(entity =>
        {
            entity.Property(e => e.ScheduledAmount).HasColumnType("numeric(12,2)");
            entity.HasIndex(e => new { e.UserId, e.RecurringPaymentId, e.OccurrenceDate }).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.Status, e.OccurrenceDate });
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_recurringpaymentoccurrences_status",
                "\"Status\" IN ('Pending', 'PartiallyPaid', 'Paid', 'Discarded', 'SettledByLoanPayoff')"));
        });

        modelBuilder.Entity<FinancialSetting>(entity =>
        {
            entity.Property(e => e.TargetStabilityFund).HasColumnType("numeric(12,2)");
            entity.Property(e => e.EssentialsAlloc).HasColumnType("numeric");
            entity.Property(e => e.GrowthAlloc).HasColumnType("numeric");
            entity.Property(e => e.StabilityAlloc).HasColumnType("numeric");
            entity.Property(e => e.RewardsAlloc).HasColumnType("numeric");
            entity.Property(e => e.StabilityOverflowRedirect)
                .HasDefaultValue("Split: Growth 50%, Rewards 50%");
            entity.Property(e => e.HideSensitive).HasDefaultValue(true);
            entity.Property(e => e.Currency).HasDefaultValue("USD");
            entity.HasIndex(e => e.UserId).IsUnique();
        });
    }

    /// <summary>Sessions, credentials, push subscriptions and category-limit alert bookkeeping.</summary>
    private void ConfigureIdentityModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.Property(e => e.IsLocked).HasDefaultValue(false);
            entity.HasIndex(e => e.Id).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasIndex(e => e.CredentialId);
            entity.HasIndex(e => new { e.UserId, e.DeviceId });
        });

        modelBuilder.Entity<WebAuthnChallenge>(entity =>
        {
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasIndex(e => e.UserId);
        });

        modelBuilder.Entity<WebAuthnCredential>(entity =>
        {
            entity.HasIndex(e => e.UserId);
        });

        modelBuilder.Entity<RecoveryCode>(entity =>
        {
            entity.HasIndex(e => e.UserId);
        });

        modelBuilder.Entity<SecurityQuestionAnswer>(entity =>
        {
            entity.HasIndex(e => new { e.UserId, e.QuestionId }).IsUnique();
        });

        modelBuilder.Entity<PushSubscription>(entity =>
        {
            // One live subscription per (user, device): re-registering a device upserts the
            // stored FCM token in place instead of accumulating stale duplicate rows.
            entity.HasIndex(e => new { e.UserId, e.DeviceId }).IsUnique();
            // Defaults match the historical single-switch behaviour for any writer that does not
            // name the channels: a registered device received bill reminders, and spending alerts
            // were a separate deliberate opt-in.
            entity.Property(e => e.BillRemindersEnabled).HasDefaultValue(true);
            entity.Property(e => e.CategoryAlertsEnabled).HasDefaultValue(false);
        });

        modelBuilder.Entity<PushReminderDelivery>(entity =>
        {
            // The claim key: an insert into this unique tuple IS the concurrency-safe "has this
            // exact reminder already been sent" check, so retries/races can never double-send.
            // Kind is part of it because a shortfall alert and an ordinary reminder are two
            // different messages that may both be owed for the same occurrence and offset.
            entity.Property(e => e.Kind).HasDefaultValue(PushReminderDeliveryKind.Reminder);
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_pushreminderdeliveries_kind",
                "\"Kind\" IN ('Reminder', 'Shortfall')"));
            entity.HasIndex(e => new
            {
                e.UserId,
                e.RecurringPaymentId,
                e.OccurrenceDate,
                e.ActualOffsetDays,
                e.SubscriptionId,
                e.Kind
            }).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.RecurringPaymentId, e.OccurrenceDate, e.SubscriptionId, e.Kind });
        });

        modelBuilder.Entity<CategoryLimitAlertEvaluation>(entity =>
        {
            entity.Property(e => e.PreviousAmount).HasColumnType("numeric(12,2)");
            entity.Property(e => e.CurrentAmount).HasColumnType("numeric(12,2)");
            entity.Property(e => e.PreviousDate).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CurrentDate).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(e => new { e.UserId, e.CreatedAt });
        });

        modelBuilder.Entity<CategoryLimitAlertEvent>(entity =>
        {
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.ExpiresAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CompletedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.AwaitingDeviceRecovery).HasDefaultValue(false);
            entity.HasIndex(e => new { e.UserId, e.CompletedAt, e.ExpiresAt });
        });

        modelBuilder.Entity<CategoryLimitAlertMilestone>(entity =>
        {
            entity.HasIndex(e => new { e.UserId, e.CycleKey, e.CategoryName, e.Milestone }).IsUnique();
            entity.HasOne<CategoryLimitAlertEvent>()
                .WithMany()
                .HasForeignKey(e => e.EventId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CategoryLimitAlertDelivery>(entity =>
        {
            entity.Property(e => e.SentAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(e => new { e.UserId, e.EventId, e.SubscriptionId }).IsUnique();
            entity.HasOne<CategoryLimitAlertEvent>()
                .WithMany()
                .HasForeignKey(e => e.EventId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PendingTwoFactor>(entity =>
        {
            entity.HasIndex(e => e.UserId);
        });
    }
}
