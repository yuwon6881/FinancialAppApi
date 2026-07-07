using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Models;

namespace FinancialAppApi.Database;

public class AppDbContext : DbContext
{
    private static readonly TransactionCategory[] DefaultCategories =
    [
        new() { Id = "cat-1", Name = "Salary" },
        new() { Id = "cat-2", Name = "Social" },
        new() { Id = "cat-3", Name = "Food" },
        new() { Id = "cat-4", Name = "Hobbies" },
        new() { Id = "cat-5", Name = "Software" },
        new() { Id = "cat-6", Name = "Investment" },
        new() { Id = "cat-7", Name = "Entertainment" },
        new() { Id = "cat-8", Name = "Transport" },
        new() { Id = "cat-9", Name = "Other" },
        new() { Id = "cat-10", Name = "Transfer" }
    ];

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.Property(e => e.Amount).HasColumnType("numeric");
        });

        modelBuilder.Entity<RecurringPayment>(entity =>
        {
            entity.Property(e => e.Amount).HasColumnType("numeric");
        });

        modelBuilder.Entity<FinancialSetting>(entity =>
        {
            entity.Property(e => e.TargetStabilityFund).HasColumnType("numeric");
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

        modelBuilder.Entity<TransactionCategory>().HasData(DefaultCategories);

        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.Property(e => e.IsLocked).HasDefaultValue(false);
        });

        modelBuilder.Entity<WishlistItem>(entity =>
        {
            entity.Property(e => e.Price).HasColumnType("numeric");
            entity.Property(e => e.Priority).HasDefaultValue("Medium");
            entity.Property(e => e.IsPurchased).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
            entity.Property(e => e.IsActive).HasDefaultValue(false);
        });
    }
}
