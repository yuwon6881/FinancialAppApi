using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using FinancialAppApi.Models;

namespace FinancialAppApi.Database;

public class AppDbContext : DbContext, IDataProtectionKeyContext
{
    public string? CurrentUserId { get; private set; }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<RecurringPayment> RecurringPayments => Set<RecurringPayment>();
    public DbSet<FinancialSetting> FinancialSettings => Set<FinancialSetting>();
    public DbSet<TransactionCategory> TransactionCategories => Set<TransactionCategory>();
    public DbSet<CategorySpendingGuide> CategorySpendingGuides => Set<CategorySpendingGuide>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<WishlistItem> WishlistItems => Set<WishlistItem>();
    public DbSet<SavingsGoal> SavingsGoals => Set<SavingsGoal>();
    public DbSet<SavingsGoalCompletion> SavingsGoalCompletions => Set<SavingsGoalCompletion>();
    public DbSet<WebAuthnCredential> WebAuthnCredentials => Set<WebAuthnCredential>();
    public DbSet<WebAuthnChallenge> WebAuthnChallenges => Set<WebAuthnChallenge>();
    public DbSet<ReceiptScanJob> ReceiptScanJobs => Set<ReceiptScanJob>();
    public DbSet<CycleBalance> CycleBalances => Set<CycleBalance>();
    public DbSet<PendingTwoFactor> PendingTwoFactors => Set<PendingTwoFactor>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<SecurityQuestionAnswer> SecurityQuestionAnswers => Set<SecurityQuestionAnswer>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<PushReminderDelivery> PushReminderDeliveries => Set<PushReminderDelivery>();
    public DbSet<InvestmentAccount> InvestmentAccounts => Set<InvestmentAccount>();
    public DbSet<InvestmentInstrument> InvestmentInstruments => Set<InvestmentInstrument>();
    public DbSet<InvestmentTransaction> InvestmentTransactions => Set<InvestmentTransaction>();
    public DbSet<InvestmentCashFlow> InvestmentCashFlows => Set<InvestmentCashFlow>();
    public DbSet<InvestmentPlan> InvestmentPlans => Set<InvestmentPlan>();
    public DbSet<MarketPriceBar> MarketPriceBars => Set<MarketPriceBar>();
    public DbSet<FxRateBar> FxRateBars => Set<FxRateBar>();
    public DbSet<MarketDataRefreshJob> MarketDataRefreshJobs => Set<MarketDataRefreshJob>();
    public DbSet<MarketDataQuotaWindow> MarketDataQuotaWindows => Set<MarketDataQuotaWindow>();
    public DbSet<InstrumentSearchCache> InstrumentSearchCaches => Set<InstrumentSearchCache>();
    public DbSet<VaultDocument> VaultDocuments => Set<VaultDocument>();
    public DbSet<TaxReliefCategoryLimit> TaxReliefCategoryLimits => Set<TaxReliefCategoryLimit>();
    public DbSet<AiConversation> AiConversations => Set<AiConversation>();
    public DbSet<AiConversationTurn> AiConversationTurns => Set<AiConversationTurn>();
    public DbSet<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey> DataProtectionKeys => Set<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey>();

    public void SetCurrentUser(string userId)
    {
        CurrentUserId = string.IsNullOrWhiteSpace(userId)
            ? throw new ArgumentException("A user id is required.", nameof(userId))
            : userId;
    }

    public string RequireCurrentUserId() => CurrentUserId
        ?? throw new InvalidOperationException("No authenticated user is associated with this database scope.");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Transaction>(entity =>
        {
            // Bounded numeric(12,2) instead of unbounded numeric: exact for money, but
            // fixed/smaller on-disk, which matters against the 500MB free storage ceiling.
            entity.Property(e => e.Amount).HasColumnType("numeric(12,2)");
            entity.Property(e => e.Date).HasColumnType("timestamp with time zone");
            entity.Property(e => e.PostedAt).HasColumnType("timestamp with time zone");
            // Calendar date is primary; PostedAt resolves the order of records on that date.
            entity.HasIndex(e => new { e.UserId, e.Date, e.PostedAt, e.LedgerCategory })
                .IsDescending(false, true, true, false);
            entity.HasIndex(e => new { e.UserId, e.WishlistItemId })
                .IsUnique()
                .HasFilter("\"WishlistItemId\" IS NOT NULL");
            entity.HasIndex(e => new { e.UserId, e.SavingsGoalId });
            // Guarantees a given recurring-payment occurrence can never be settled twice
            // (normal confirmation racing pay-early, pay-early retried, etc.). Partial so
            // legacy/manual transactions (either column null) are exempt.
            entity.HasIndex(e => new { e.UserId, e.RecurringPaymentId, e.RecurringOccurrenceDate })
                .IsUnique()
                .HasFilter("\"RecurringPaymentId\" IS NOT NULL AND \"RecurringOccurrenceDate\" IS NOT NULL");
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
            entity.HasIndex(e => e.UserId);
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("ck_recurringpayments_amount_nonzero", "\"Amount\" <> 0");
                t.HasCheckConstraint("ck_recurringpayments_pushremindermode", "\"PushReminderMode\" IN ('Once', 'Daily')");
                t.HasCheckConstraint("ck_recurringpayments_pushreminderleaddays", "\"PushReminderLeadDays\" IN (1, 2, 3, 7)");
            });
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
            entity.Property(e => e.VibrationEnabled).HasDefaultValue(true);
            entity.HasIndex(e => e.UserId).IsUnique();
        });

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
        });

        modelBuilder.Entity<PushReminderDelivery>(entity =>
        {
            // The claim key: an insert into this unique tuple IS the concurrency-safe "has this
            // exact reminder already been sent" check, so retries/races can never double-send.
            entity.HasIndex(e => new
            {
                e.UserId,
                e.RecurringPaymentId,
                e.OccurrenceDate,
                e.ActualOffsetDays,
                e.SubscriptionId
            }).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.RecurringPaymentId, e.OccurrenceDate, e.SubscriptionId });
        });

        modelBuilder.Entity<PendingTwoFactor>(entity =>
        {
            entity.HasIndex(e => e.UserId);
        });

        modelBuilder.Entity<WishlistItem>(entity =>
        {
            entity.Property(e => e.Price).HasColumnType("numeric(12,2)");
            entity.Property(e => e.Priority).HasDefaultValue("Medium");
            entity.Property(e => e.IsPurchased).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
            entity.Property(e => e.IsActive).HasDefaultValue(false);
            entity.HasIndex(e => new { e.UserId, e.PurchaseTransactionId });
            entity.Property(e => e.IsPurchased).IsConcurrencyToken();
            // Unique when present so a replayed offline create dedupes to the same row; the
            // filter keeps pre-existing rows (null ClientKey) exempt from the uniqueness constraint.
            entity.HasIndex(e => new { e.UserId, e.ClientKey })
                .IsUnique()
                .HasFilter("\"ClientKey\" IS NOT NULL");
        });

        modelBuilder.Entity<SavingsGoal>(entity =>
        {
            entity.Property(e => e.TargetAmount).HasColumnType("numeric(12,2)");
            entity.Property(e => e.EarmarkedAmount)
                .HasColumnType("numeric(12,2)")
                .HasDefaultValue(0m)
                .IsConcurrencyToken();
            entity.Property(e => e.CycleFundedAmount).HasColumnType("numeric(12,2)").HasDefaultValue(0m);
            entity.Property(e => e.TargetDate).HasColumnType("date");
            entity.Property(e => e.Priority).HasDefaultValue("Medium");
            entity.Property(e => e.Status).HasDefaultValue(SavingsGoalStatus.Active);
            entity.Property(e => e.IsRecurring).HasDefaultValue(false);
            entity.Property(e => e.RecurrenceMonths).HasDefaultValue(12);
            entity.Property(e => e.RecurrenceDayOfMonth).HasColumnType("integer");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
            // The list is always read as "active goals in funding order", so index the status and
            // deadline together rather than making every page load sort the whole table.
            entity.HasIndex(e => new { e.UserId, e.Status, e.TargetDate });
            // Unique when present so a replayed offline create dedupes to the same row; the filter
            // keeps pre-existing rows (null ClientKey) exempt from the uniqueness constraint.
            entity.HasIndex(e => new { e.UserId, e.ClientKey })
                .IsUnique()
                .HasFilter("\"ClientKey\" IS NOT NULL");
            entity.ToTable(t =>
            {
                // Defence in depth for the earmark invariant. SavingsGoalService checks the pool-wide
                // rule (SUM(earmarked) <= rewards balance), which the database cannot see; these two
                // hold the per-row half of it so no code path can persist a nonsensical earmark.
                t.HasCheckConstraint("ck_savingsgoals_target_positive", "\"TargetAmount\" > 0");
                t.HasCheckConstraint("ck_savingsgoals_earmark_within_target",
                    "\"EarmarkedAmount\" >= 0 AND \"EarmarkedAmount\" <= \"TargetAmount\"");
            });
        });

        modelBuilder.Entity<ReceiptScanJob>(entity =>
        {
            entity.Property(e => e.Status).HasDefaultValue("queued");
            entity.Property(e => e.MimeType).HasDefaultValue("image/jpeg");
            entity.Property(e => e.ScanType).HasDefaultValue("receipt");
            entity.Property(e => e.StorageObjectPath).HasMaxLength(512);
            entity.HasIndex(e => new { e.UserId, e.Status, e.UpdatedAt });
            // Supports lease recovery and retention cleanup without scanning image/result data.
            entity.HasIndex(e => new { e.Status, e.UpdatedAt });
        });

        modelBuilder.Entity<CycleBalance>(entity =>
        {
            entity.HasKey(e => new { e.UserId, e.Year, e.MonthIndex });
            entity.Property(e => e.EssentialsBalance).HasColumnType("numeric(12,2)");
            entity.Property(e => e.GrowthBalance).HasColumnType("numeric(12,2)");
            entity.Property(e => e.StabilityBalance).HasColumnType("numeric(12,2)");
            entity.Property(e => e.RewardsBalance).HasColumnType("numeric(12,2)");
        });

        modelBuilder.Entity<TransactionCategory>(entity =>
        {
            entity.Property(e => e.CycleLimit).HasColumnType("numeric(12,2)");
            entity.HasIndex(e => new { e.UserId, e.Name }).IsUnique();
        });

        modelBuilder.Entity<CategorySpendingGuide>(entity =>
        {
            entity.Property(e => e.LimitAmount).HasColumnType("numeric(12,2)");
            entity.HasIndex(e => new { e.UserId, e.CategoryName, e.EffectiveFromCycleKey }).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.EffectiveFromCycleKey });
        });

        modelBuilder.Entity<InvestmentAccount>(entity =>
        {
            entity.HasIndex(e => new { e.UserId, e.Name }).IsUnique();
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
        });

        modelBuilder.Entity<InvestmentInstrument>(entity =>
        {
            entity.HasIndex(e => new { e.UserId, e.Symbol, e.ProviderMic }).IsUnique();
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_investmentinstruments_allocationsleeve",
                "\"AllocationSleeve\" IS NULL OR \"AllocationSleeve\" IN ('USEquity', 'InternationalExUS', 'Bonds')"));
        });

        modelBuilder.Entity<InvestmentPlan>(entity =>
        {
            entity.Property(e => e.UsEquityTarget).HasColumnType("numeric(5,2)");
            entity.Property(e => e.InternationalExUsTarget).HasColumnType("numeric(5,2)");
            entity.Property(e => e.BondsTarget).HasColumnType("numeric(5,2)");
            entity.Property(e => e.WatchDrift).HasColumnType("numeric(5,2)");
            entity.Property(e => e.AlertDrift).HasColumnType("numeric(5,2)");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(e => e.UserId).IsUnique();
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("ck_investmentplans_targets_positive",
                    "\"UsEquityTarget\" > 0 AND \"InternationalExUsTarget\" > 0 AND \"BondsTarget\" > 0");
                t.HasCheckConstraint("ck_investmentplans_targets_total",
                    "\"UsEquityTarget\" + \"InternationalExUsTarget\" + \"BondsTarget\" = 100");
                t.HasCheckConstraint("ck_investmentplans_drift_bands",
                    "\"WatchDrift\" > 0 AND \"AlertDrift\" > \"WatchDrift\" AND \"AlertDrift\" <= 100");
            });
        });

        modelBuilder.Entity<InvestmentTransaction>(entity =>
        {
            entity.Property(e => e.TradeDate).HasColumnType("date");
            entity.Property(e => e.Units).HasColumnType("numeric(28,10)");
            entity.Property(e => e.UnitPrice).HasColumnType("numeric(28,10)");
            entity.Property(e => e.CashAmount).HasColumnType("numeric(28,10)");
            entity.Property(e => e.Fees).HasColumnType("numeric(28,10)");
            entity.Property(e => e.Taxes).HasColumnType("numeric(28,10)");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(e => new { e.UserId, e.AccountId, e.InstrumentId, e.TradeDate });
            entity.HasOne(e => e.Account).WithMany().HasForeignKey(e => e.AccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Instrument).WithMany().HasForeignKey(e => e.InstrumentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InvestmentCashFlow>(entity =>
        {
            entity.Property(e => e.Date).HasColumnType("date");
            entity.Property(e => e.Amount).HasColumnType("numeric(28,10)");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(e => new { e.UserId, e.AccountId, e.Date });
            entity.HasOne(e => e.Account).WithMany().HasForeignKey(e => e.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MarketPriceBar>(entity =>
        {
            entity.Property(e => e.MarketDate).HasColumnType("date");
            entity.Property(e => e.Close).HasColumnType("numeric(28,10)");
            entity.Property(e => e.FetchedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(e => new { e.Provider, e.Symbol, e.Mic, e.MarketDate }).IsUnique();
        });

        modelBuilder.Entity<FxRateBar>(entity =>
        {
            entity.Property(e => e.MarketDate).HasColumnType("date");
            entity.Property(e => e.Rate).HasColumnType("numeric(28,10)");
            entity.Property(e => e.FetchedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(e => new { e.Provider, e.BaseCurrency, e.QuoteCurrency, e.MarketDate }).IsUnique();
        });

        modelBuilder.Entity<MarketDataRefreshJob>(entity =>
        {
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CompletedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(e => new { e.UserId, e.Status, e.UpdatedAt });
        });

        modelBuilder.Entity<MarketDataQuotaWindow>(entity =>
        {
            entity.Property(e => e.WindowStart).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.Version).IsRowVersion();
            entity.HasIndex(e => new { e.Scope, e.WindowStart }).IsUnique();
        });

        modelBuilder.Entity<InstrumentSearchCache>(entity =>
        {
            entity.Property(e => e.ExpiresAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(e => e.NormalizedQuery).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
        });

        modelBuilder.Entity<VaultDocument>(entity =>
        {
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.AmountConfidence).HasPrecision(5, 4);
            entity.HasIndex(e => new { e.UserId, e.UploadedAt, e.Id })
                .IsDescending(false, true, true);
            entity.HasIndex(e => new { e.UserId, e.TaxYear, e.UploadedAt, e.Id })
                .IsDescending(false, false, true, true);
            entity.HasIndex(e => new { e.UserId, e.TransactionId });
            entity.HasIndex(e => new { e.UserId, e.ClientKey })
                .IsUnique()
                .HasFilter("\"ClientKey\" IS NOT NULL");
        });

        modelBuilder.Entity<TaxReliefCategoryLimit>(entity =>
        {
            entity.Property(e => e.Limit).HasPrecision(18, 2);
            entity.HasIndex(e => new { e.UserId, e.TaxYear, e.CategoryId }).IsUnique();
        });

        modelBuilder.Entity<SavingsGoalCompletion>(entity =>
        {
            entity.Property(e => e.PreviousTargetDate).HasColumnType("date");
            entity.Property(e => e.ResultingTargetDate).HasColumnType("date");
            entity.Property(e => e.PreviousEarmarkedAmount).HasColumnType("numeric(12,2)");
            entity.Property(e => e.PreviousCycleFundedAmount).HasColumnType("numeric(12,2)");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(e => new { e.UserId, e.SavingsGoalId, e.CreatedAt });
        });

        modelBuilder.Entity<AiConversation>(entity =>
        {
            entity.Property(e => e.Version).IsConcurrencyToken();
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(e => e.UserId).IsUnique();
        });

        modelBuilder.Entity<AiConversationTurn>(entity =>
        {
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(e => new { e.ConversationId, e.ClientTurnId }).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.ConversationId, e.CreatedAt });
            entity.HasOne(e => e.Conversation)
                .WithMany(e => e.Turns)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        ConfigureUserOwnership(modelBuilder.Entity<Transaction>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<RecurringPayment>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<FinancialSetting>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<TransactionCategory>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<CategorySpendingGuide>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<WishlistItem>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<SavingsGoal>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<SavingsGoalCompletion>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<CycleBalance>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<UserSession>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<WebAuthnCredential>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<WebAuthnChallenge>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<ReceiptScanJob>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<PendingTwoFactor>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<RecoveryCode>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<SecurityQuestionAnswer>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<PushSubscription>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<PushReminderDelivery>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<InvestmentAccount>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<InvestmentInstrument>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<InvestmentTransaction>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<InvestmentCashFlow>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<InvestmentPlan>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<MarketDataRefreshJob>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<VaultDocument>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<TaxReliefCategoryLimit>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<AiConversation>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<AiConversationTurn>(), applyQueryFilter: true);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyUserOwnership();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ApplyUserOwnership();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ConfigureUserOwnership<TEntity>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity,
        bool applyQueryFilter)
        where TEntity : class, IUserOwnedEntity
    {
        entity.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        if (applyQueryFilter)
        {
            entity.HasQueryFilter(e => CurrentUserId != null && e.UserId == CurrentUserId);
        }
    }

    /// <summary>
    /// Currency codes carry an invariant -- uppercase ISO-4217 -- that used to be re-asserted
    /// ad hoc at each call site. Enforcing it once on write means a lowercase code can never
    /// reach a comparison that silently fails to match (e.g. FX resolution in the portfolio
    /// service, which returns null totals rather than an error when a lookup misses).
    /// </summary>
    private void NormalizeCurrencies()
    {
        static string Upper(string value) => value.Trim().ToUpperInvariant();

        foreach (var entry in ChangeTracker.Entries<InvestmentAccount>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.BaseCurrency = Upper(entry.Entity.BaseCurrency);
        }

        foreach (var entry in ChangeTracker.Entries<InvestmentInstrument>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.Currency = Upper(entry.Entity.Currency);
        }

        foreach (var entry in ChangeTracker.Entries<InvestmentCashFlow>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.Currency = Upper(entry.Entity.Currency);
            if (entry.Entity.ToCurrency is not null) entry.Entity.ToCurrency = Upper(entry.Entity.ToCurrency);
        }

        foreach (var entry in ChangeTracker.Entries<FxRateBar>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.BaseCurrency = Upper(entry.Entity.BaseCurrency);
            entry.Entity.QuoteCurrency = Upper(entry.Entity.QuoteCurrency);
        }

        foreach (var entry in ChangeTracker.Entries<FinancialSetting>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.Currency = Upper(entry.Entity.Currency);
        }
    }

    private void ApplyUserOwnership()
    {
        foreach (var entry in ChangeTracker.Entries<AppUser>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.Username = entry.Entity.Username.Trim();
            entry.Entity.NormalizedUsername = entry.Entity.Username.ToUpperInvariant();
        }

        NormalizeCurrencies();

        foreach (var entry in ChangeTracker.Entries<IUserOwnedEntity>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.State == EntityState.Added && string.IsNullOrWhiteSpace(entry.Entity.UserId))
            {
                entry.Entity.UserId = RequireCurrentUserId();
            }

            if (CurrentUserId != null && !string.Equals(entry.Entity.UserId, CurrentUserId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A request cannot write data owned by another user.");
            }

            if (string.IsNullOrWhiteSpace(entry.Entity.UserId))
            {
                throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} must have a user owner.");
            }
        }
    }
}
