using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Models;

namespace FinancialAppApi.Database;

public partial class AppDbContext
{
    /// <summary>Wishlist items, savings goals, loans and receipt scan jobs.</summary>
    private void ConfigureCommitmentModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WishlistItem>(entity =>
        {
            entity.Property(e => e.Price).HasColumnType("numeric(12,2)");
            entity.Property(e => e.Priority).HasDefaultValue("Medium");
            entity.Property(e => e.IsPurchased).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
            entity.Property(e => e.IsActive).HasDefaultValue(false);
            entity.HasIndex(e => new { e.UserId, e.PurchaseTransactionId });
            entity.HasIndex(e => e.UserId)
                .IsUnique()
                .HasDatabaseName("IX_WishlistItems_UserId_FocusedOpen")
                .HasFilter("\"IsActive\" = TRUE AND \"IsPurchased\" = FALSE");
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
            entity.Property(e => e.FundingBucket)
                .HasMaxLength(20)
                .HasDefaultValue(SavingsGoalFundingBucket.Rewards);
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
                t.HasCheckConstraint("ck_savingsgoals_fundingbucket",
                    $"\"FundingBucket\" IN ('{SavingsGoalFundingBucket.Essentials}', '{SavingsGoalFundingBucket.Rewards}')");
            });
        });

        modelBuilder.Entity<Loan>(entity =>
        {
            entity.Property(e => e.OpeningPrincipal).HasColumnType("numeric(12,2)");
            entity.Property(e => e.AnnualRatePercent).HasColumnType("numeric(7,4)");
            entity.Property(e => e.RateBasis).HasDefaultValue(LoanRateBasis.Yearly);
            entity.Property(e => e.TrackingStartDate).HasColumnType("date");
            entity.Property(e => e.InterestMethod).HasDefaultValue(LoanInterestMethod.ReducingBalance);
            entity.Property(e => e.ScheduleStartDate).HasColumnType("date");
            entity.Property(e => e.ScheduleStatus).HasDefaultValue(LoanScheduleStatus.Incomplete);
            entity.HasIndex(e => new { e.UserId, e.RecurringPaymentId }).IsUnique();
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("ck_loans_openingprincipal", "\"OpeningPrincipal\" > 0");
                t.HasCheckConstraint("ck_loans_annualrate", "\"AnnualRatePercent\" >= 0 AND \"AnnualRatePercent\" <= 100");
                t.HasCheckConstraint("ck_loans_term", "\"TermPeriods\" > 0 AND \"TermPeriods\" <= 360");
                t.HasCheckConstraint("ck_loans_interestmethod",
                    $"\"InterestMethod\" IN ('{LoanInterestMethod.ReducingBalance}', '{LoanInterestMethod.Flat}', '{LoanInterestMethod.ReducingBalanceDaily}', '{LoanInterestMethod.InterestOnly}')");
                t.HasCheckConstraint("ck_loans_ratebasis",
                    $"\"RateBasis\" IN ('{LoanRateBasis.Yearly}', '{LoanRateBasis.Monthly}')");
                t.HasCheckConstraint("ck_loans_schedulestatus",
                    $"\"ScheduleStatus\" IN ('{LoanScheduleStatus.Complete}', '{LoanScheduleStatus.Incomplete}')");
                t.HasCheckConstraint("ck_loans_scheduledueday",
                    "\"ScheduleDueDay\" IS NULL OR \"ScheduleDueDay\" BETWEEN 1 AND 31");
            });
        });

        modelBuilder.Entity<LoanRepaymentAction>(entity =>
        {
            entity.Property(e => e.LenderQuoteAmount).HasColumnType("numeric(12,2)");
            entity.Property(e => e.EffectiveDate).HasColumnType("date");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(e => new { e.UserId, e.LoanId, e.CreatedAt });
            entity.HasIndex(e => new { e.UserId, e.RecurringPaymentId });
            // A loan can be paid off once. LoanService keys its replay override by LoanId, so a second
            // settlement row would both contradict the first and make that lookup throw on a duplicate
            // key, taking the whole loan list down rather than just that loan.
            entity.HasIndex(e => new { e.UserId, e.LoanId })
                .IsUnique()
                .HasFilter($"\"Kind\" = '{LoanRepaymentActionKind.FullSettlement}'");
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "ck_loanrepaymentactions_kind",
                    "\"Kind\" IN ('AdvanceCycles', 'FullSettlement')");
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
    }

    /// <summary>Cycle balances, stability plan revisions and transaction categories.</summary>
    private void ConfigureCycleModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CycleBalance>(entity =>
        {
            entity.HasKey(e => new { e.UserId, e.Year, e.MonthIndex });
            entity.Property(e => e.EssentialsBalance).HasColumnType("numeric(12,2)");
            entity.Property(e => e.GrowthBalance).HasColumnType("numeric(12,2)");
            entity.Property(e => e.StabilityBalance).HasColumnType("numeric(12,2)");
            entity.Property(e => e.StabilityReloadOutstanding).HasColumnType("numeric(12,2)");
            entity.Property(e => e.StabilityReloadOldestDate).HasColumnType("date");
            entity.Property(e => e.StabilityReloadObligations).HasColumnType("jsonb");
            entity.Property(e => e.RewardsBalance).HasColumnType("numeric(12,2)");
        });

        modelBuilder.Entity<StabilityPlanRevision>(entity =>
        {
            entity.Property(e => e.EffectiveAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.TargetStabilityFund).HasColumnType("numeric(12,2)");
            entity.Property(e => e.StabilityAlloc).HasColumnType("numeric(8,6)");
            entity.HasIndex(e => new { e.UserId, e.EffectiveAt, e.Id });
        });

        modelBuilder.Entity<TransactionCategory>(entity =>
        {
            entity.Property(e => e.CycleLimit).HasColumnType("numeric(12,2)");
            entity.Property(e => e.Type).HasDefaultValue(CategoryFlowType.Both);
            entity.HasIndex(e => new { e.UserId, e.Name }).IsUnique();
        });

        modelBuilder.Entity<CategorySpendingGuide>(entity =>
        {
            entity.Property(e => e.LimitAmount).HasColumnType("numeric(12,2)");
            entity.HasIndex(e => new { e.UserId, e.CategoryName, e.EffectiveFromCycleKey }).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.EffectiveFromCycleKey });
        });
    }
}
