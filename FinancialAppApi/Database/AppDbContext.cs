using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using FinancialAppApi.Models;

namespace FinancialAppApi.Database;

public class AppDbContext : DbContext, IDataProtectionKeyContext
{
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
    public DbSet<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey> DataProtectionKeys => Set<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey>();

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
            entity.HasIndex(e => new { e.Date, e.PostedAt, e.LedgerCategory })
                .IsDescending(true, true, false);
            entity.HasIndex(e => e.WishlistItemId)
                .IsUnique()
                .HasFilter("\"WishlistItemId\" IS NOT NULL");
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.Property(e => e.SingletonKey).HasDefaultValue(1);
            entity.HasIndex(e => e.SingletonKey).IsUnique();
        });

        modelBuilder.Entity<RecurringPayment>(entity =>
        {
            entity.Property(e => e.Amount).HasColumnType("numeric(12,2)");
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
            entity.Property(e => e.DarkMode).HasDefaultValue(false);
            entity.Property(e => e.HideSensitive).HasDefaultValue(true);
            entity.Property(e => e.Currency).HasDefaultValue("USD");
            entity.Property(e => e.VibrationEnabled).HasDefaultValue(true);
        });

        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.Property(e => e.IsLocked).HasDefaultValue(false);
            entity.HasIndex(e => e.Id).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasIndex(e => e.CredentialId);
            entity.HasIndex(e => new { e.Username, e.DeviceId });
        });

        modelBuilder.Entity<WebAuthnChallenge>(entity =>
        {
            entity.HasIndex(e => e.ExpiresAt);
        });

        modelBuilder.Entity<WebAuthnCredential>(entity =>
        {
            entity.HasIndex(e => e.Username);
        });

        modelBuilder.Entity<RecoveryCode>(entity =>
        {
            entity.HasIndex(e => e.Username);
        });

        modelBuilder.Entity<WishlistItem>(entity =>
        {
            entity.Property(e => e.Price).HasColumnType("numeric(12,2)");
            entity.Property(e => e.Priority).HasDefaultValue("Medium");
            entity.Property(e => e.IsPurchased).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
            entity.Property(e => e.IsActive).HasDefaultValue(false);
            entity.HasIndex(e => e.PurchaseTransactionId);
            entity.Property(e => e.IsPurchased).IsConcurrencyToken();
            // Unique when present so a replayed offline create dedupes to the same row; the
            // filter keeps pre-existing rows (null ClientKey) exempt from the uniqueness constraint.
            entity.HasIndex(e => e.ClientKey)
                .IsUnique()
                .HasFilter("\"ClientKey\" IS NOT NULL");
        });

        modelBuilder.Entity<ReceiptScanJob>(entity =>
        {
            entity.Property(e => e.Status).HasDefaultValue("queued");
            entity.Property(e => e.MimeType).HasDefaultValue("image/jpeg");
            // Supports lease recovery and retention cleanup without scanning image/result data.
            entity.HasIndex(e => new { e.Status, e.UpdatedAt });
        });

        modelBuilder.Entity<CycleBalance>(entity =>
        {
            entity.HasKey(e => new { e.Year, e.MonthIndex });
            entity.Property(e => e.EssentialsBalance).HasColumnType("numeric(12,2)");
            entity.Property(e => e.GrowthBalance).HasColumnType("numeric(12,2)");
            entity.Property(e => e.StabilityBalance).HasColumnType("numeric(12,2)");
            entity.Property(e => e.RewardsBalance).HasColumnType("numeric(12,2)");
        });
    }
}
