const fs = require('fs');

const tables = [
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
];

function getAllFiles(dirPath, arrayOfFiles) {
  const files = fs.readdirSync(dirPath);

  arrayOfFiles = arrayOfFiles || [];

  files.forEach(function(file) {
    if (file === 'bin' || file === 'obj' || file.startsWith('.')) return;
    const fullPath = dirPath + "/" + file;
    if (fs.statSync(fullPath).isDirectory()) {
      arrayOfFiles = getAllFiles(fullPath, arrayOfFiles);
    } else {
      if (fullPath.endsWith('.cs') && !fullPath.includes('Migrations') && !fullPath.includes('Models') && !fullPath.includes('Database') && !fullPath.includes('Tests')) {
          arrayOfFiles.push(fullPath);
      }
    }
  });

  return arrayOfFiles;
}

const allCodeFiles = getAllFiles('FinancialAppApi');

for (const table of tables) {
    let found = false;
    for (const file of allCodeFiles) {
        const content = fs.readFileSync(file, 'utf8');
        if (content.includes(`.${table}`) || content.includes(` ${table} `)) {
            found = true;
            break;
        }
    }
    if (!found) {
        console.log(`UNUSED DBSET (Backend): ${table}`);
    }
}
