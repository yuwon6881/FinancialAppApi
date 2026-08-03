$tables = @(
"Transactions", "RecurringPayments", "FinancialSettings", "TransactionCategories",
"CategorySpendingGuides", "AppUsers", "UserSessions", "WishlistItems",
"SavingsGoals", "SavingsGoalCompletions", "WebAuthnCredentials",
"WebAuthnChallenges", "ReceiptScanJobs", "CycleBalances", "PendingTwoFactors",
"RecoveryCodes", "SecurityQuestionAnswers", "PushSubscriptions",
"PushReminderDeliveries", "InvestmentAccounts", "InvestmentInstruments",
"InvestmentTransactions", "InvestmentCashFlows", "InvestmentPlans",
"MarketPriceBars", "FxRateBars", "ManualPriceOverrides",
"MarketDataRefreshJobs", "MarketDataQuotaWindows", "InstrumentSearchCaches",
"VaultDocuments", "TaxReliefCategoryLimits", "AiConversations",
"AiConversationTurns", "DataProtectionKeys"
)

foreach ($table in $tables) {
    # Check if table usage exists in FinancialAppApi controllers or services
    $res = git grep -i "\.$table" -- "FinancialAppApi/Controllers" "FinancialAppApi/Services"
    if (-not $res) {
        Write-Output "UNUSED DBSET: $table"
    }
}

foreach ($table in $tables) {
    # Check if singular model name is used in frontend
    # we convert plural to singular roughly for some of them, e.g. ManualPriceOverride
    if ($table.EndsWith("ies")) {
        $singular = $table.Substring(0, $table.Length - 3) + "y"
    } elseif ($table.EndsWith("s")) {
        $singular = $table.Substring(0, $table.Length - 1)
    } else {
        $singular = $table
    }
    
    $res = git grep -i "$singular" -- "../FinancialApp/src"
    if (-not $res) {
        Write-Output "UNUSED FRONTEND MODEL: $singular"
    }
}
