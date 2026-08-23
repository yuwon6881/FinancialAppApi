using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Models;

namespace FinancialAppApi.Database;

public partial class AppDbContext
{
    /// <summary>Investment accounts, instruments, activity, plans and market data.</summary>
    private void ConfigureInvestmentModel(ModelBuilder modelBuilder)
    {
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

        modelBuilder.Entity<InvestmentInstrumentMarketMapping>(entity =>
        {
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.HasOne(e => e.InvestmentInstrument)
                .WithMany(e => e.MarketMappings)
                .HasForeignKey(e => e.InvestmentInstrumentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.UserId, e.InvestmentInstrumentId, e.ProviderId }).IsUnique();
            entity.HasIndex(e => new { e.ProviderId, e.ExternalInstrumentId });
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
            entity.HasIndex(e => new { e.Provider, e.ExternalInstrumentId, e.MarketDate }).IsUnique();
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
            entity.HasIndex(e => new { e.ProviderId, e.NormalizedQuery }).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
        });
    }
}
