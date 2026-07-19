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
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<WishlistItem> WishlistItems => Set<WishlistItem>();
    public DbSet<WebAuthnCredential> WebAuthnCredentials => Set<WebAuthnCredential>();
    public DbSet<WebAuthnChallenge> WebAuthnChallenges => Set<WebAuthnChallenge>();
    public DbSet<ReceiptScanJob> ReceiptScanJobs => Set<ReceiptScanJob>();
    public DbSet<CycleBalance> CycleBalances => Set<CycleBalance>();
    public DbSet<PendingTwoFactor> PendingTwoFactors => Set<PendingTwoFactor>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<SecurityQuestionAnswer> SecurityQuestionAnswers => Set<SecurityQuestionAnswer>();
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
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasIndex(e => e.RegistrationSlot).IsUnique();
            entity.HasIndex(e => e.NormalizedUsername).IsUnique();
        });

        modelBuilder.Entity<RecurringPayment>(entity =>
        {
            entity.Property(e => e.Amount).HasColumnType("numeric(12,2)");
            entity.HasIndex(e => e.UserId);
            entity.ToTable(t => t.HasCheckConstraint("ck_recurringpayments_amount_nonzero", "\"Amount\" <> 0"));
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

        modelBuilder.Entity<ReceiptScanJob>(entity =>
        {
            entity.Property(e => e.Status).HasDefaultValue("queued");
            entity.Property(e => e.MimeType).HasDefaultValue("image/jpeg");
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
            entity.HasIndex(e => new { e.UserId, e.Name }).IsUnique();
        });

        ConfigureUserOwnership(modelBuilder.Entity<Transaction>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<RecurringPayment>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<FinancialSetting>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<TransactionCategory>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<WishlistItem>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<CycleBalance>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<UserSession>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<WebAuthnCredential>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<WebAuthnChallenge>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<ReceiptScanJob>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<PendingTwoFactor>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<RecoveryCode>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<SecurityQuestionAnswer>(), applyQueryFilter: false);
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

    private void ApplyUserOwnership()
    {
        foreach (var entry in ChangeTracker.Entries<AppUser>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.Username = entry.Entity.Username.Trim();
            entry.Entity.NormalizedUsername = entry.Entity.Username.ToUpperInvariant();
        }

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
