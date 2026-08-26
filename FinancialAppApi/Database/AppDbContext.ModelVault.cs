using Microsoft.EntityFrameworkCore;
using FinancialAppApi.Models;

namespace FinancialAppApi.Database;

public partial class AppDbContext
{
    /// <summary>Vault documents, tax relief limits and AI conversation history.</summary>
    private void ConfigureVaultAndAiModel(ModelBuilder modelBuilder)
    {
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
            entity.Property(e => e.ReversedAt).HasColumnType("timestamp with time zone");
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
            entity.Property(e => e.CompletedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.ActionsResolvedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.ActionsDismissedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.Status).HasDefaultValue("Completed");
            entity.HasIndex(e => new { e.ConversationId, e.ClientTurnId }).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.ConversationId, e.CreatedAt });
            entity.HasOne(e => e.Conversation)
                .WithMany(e => e.Turns)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>Per-entity ownership: the global query filters and which sets opt out of them.</summary>
    private void ConfigureTenancy(ModelBuilder modelBuilder)
    {
        ConfigureUserOwnership(modelBuilder.Entity<Transaction>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<LedgerAccount>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<RecurringPayment>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<RecurringPaymentOccurrence>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<FinancialSetting>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<TransactionCategory>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<CategorySpendingGuide>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<WishlistItem>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<SavingsGoal>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<SavingsGoalCompletion>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<SavingsGoalFundingAction>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<Loan>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<LoanRepaymentAction>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<CycleBalance>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<StabilityPlanRevision>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<UserSession>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<WebAuthnCredential>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<WebAuthnChallenge>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<ReceiptScanJob>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<PendingTwoFactor>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<RecoveryCode>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<SecurityQuestionAnswer>(), applyQueryFilter: false);
        ConfigureUserOwnership(modelBuilder.Entity<PushSubscription>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<PushReminderDelivery>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<CategoryLimitAlertEvaluation>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<CategoryLimitAlertEvent>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<CategoryLimitAlertMilestone>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<CategoryLimitAlertDelivery>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<InvestmentAccount>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<InvestmentInstrument>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<InvestmentInstrumentMarketMapping>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<InvestmentTransaction>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<InvestmentCashFlow>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<InvestmentPlan>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<MarketDataRefreshJob>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<VaultDocument>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<TaxReliefCategoryLimit>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<AiConversation>(), applyQueryFilter: true);
        ConfigureUserOwnership(modelBuilder.Entity<AiConversationTurn>(), applyQueryFilter: true);
    }
}
