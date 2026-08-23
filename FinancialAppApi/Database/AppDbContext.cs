using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using FinancialAppApi.Models;

namespace FinancialAppApi.Database;

public partial class AppDbContext : DbContext, IDataProtectionKeyContext
{
    private readonly HashSet<Transaction> _capturedCategoryLimitTransactions =
        new(ReferenceEqualityComparer.Instance);

    public string? CurrentUserId { get; private set; }

    // Category cleanup deliberately reclassifies many historical rows while the user is already
    // looking at that operation. Its caller sets this for that save so the next ordinary ledger
    // mutation, rather than the cleanup itself, remains the notification boundary.
    public bool SuppressCategoryLimitAlertCapture { get; set; }

    // Set the moment this context writes an evaluation row, and read by CategoryLimitAlertMiddleware
    // to decide whether the post-response callback has anything to do at all. Without it every
    // authenticated response -- including plain GETs -- paid a scope plus two or three queries to
    // discover there was no work.
    public bool HasCapturedCategoryLimitEvaluations { get; private set; }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
    public DbSet<RecurringPayment> RecurringPayments => Set<RecurringPayment>();
    public DbSet<RecurringPaymentOccurrence> RecurringPaymentOccurrences => Set<RecurringPaymentOccurrence>();
    public DbSet<FinancialSetting> FinancialSettings => Set<FinancialSetting>();
    public DbSet<TransactionCategory> TransactionCategories => Set<TransactionCategory>();
    public DbSet<CategorySpendingGuide> CategorySpendingGuides => Set<CategorySpendingGuide>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<WishlistItem> WishlistItems => Set<WishlistItem>();
    public DbSet<SavingsGoal> SavingsGoals => Set<SavingsGoal>();
    public DbSet<SavingsGoalCompletion> SavingsGoalCompletions => Set<SavingsGoalCompletion>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<LoanRepaymentAction> LoanRepaymentActions => Set<LoanRepaymentAction>();
    public DbSet<WebAuthnCredential> WebAuthnCredentials => Set<WebAuthnCredential>();
    public DbSet<WebAuthnChallenge> WebAuthnChallenges => Set<WebAuthnChallenge>();
    public DbSet<ReceiptScanJob> ReceiptScanJobs => Set<ReceiptScanJob>();
    public DbSet<CycleBalance> CycleBalances => Set<CycleBalance>();
    public DbSet<StabilityPlanRevision> StabilityPlanRevisions => Set<StabilityPlanRevision>();
    public DbSet<PendingTwoFactor> PendingTwoFactors => Set<PendingTwoFactor>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<SecurityQuestionAnswer> SecurityQuestionAnswers => Set<SecurityQuestionAnswer>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<PushReminderDelivery> PushReminderDeliveries => Set<PushReminderDelivery>();
    public DbSet<CategoryLimitAlertEvaluation> CategoryLimitAlertEvaluations => Set<CategoryLimitAlertEvaluation>();
    public DbSet<CategoryLimitAlertEvent> CategoryLimitAlertEvents => Set<CategoryLimitAlertEvent>();
    public DbSet<CategoryLimitAlertMilestone> CategoryLimitAlertMilestones => Set<CategoryLimitAlertMilestone>();
    public DbSet<CategoryLimitAlertDelivery> CategoryLimitAlertDeliveries => Set<CategoryLimitAlertDelivery>();
    public DbSet<InvestmentAccount> InvestmentAccounts => Set<InvestmentAccount>();
    public DbSet<InvestmentInstrument> InvestmentInstruments => Set<InvestmentInstrument>();
    public DbSet<InvestmentInstrumentMarketMapping> InvestmentInstrumentMarketMappings => Set<InvestmentInstrumentMarketMapping>();
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

}
