const fs = require('fs');

const tables = [
"Transaction", "RecurringPayment", "FinancialSetting", "TransactionCategory",
"CategorySpendingGuide", "AppUser", "UserSession", "WishlistItem",
"SavingsGoal", "SavingsGoalCompletion", "WebAuthnCredential",
"WebAuthnChallenge", "ReceiptScanJob", "CycleBalance", "PendingTwoFactor",
"RecoveryCode", "SecurityQuestionAnswer", "PushSubscription",
"PushReminderDelivery", "InvestmentAccount", "InvestmentInstrument",
"InvestmentTransaction", "InvestmentCashFlow", "InvestmentPlan",
"MarketPriceBar", "FxRateBar", "ManualPriceOverride",
"MarketDataRefreshJob", "MarketDataQuotaWindow", "InstrumentSearchCache",
"VaultDocument", "TaxReliefCategoryLimit", "AiConversation",
"AiConversationTurn"
];

function getAllFiles(dirPath, arrayOfFiles) {
  const files = fs.readdirSync(dirPath);

  arrayOfFiles = arrayOfFiles || [];

  files.forEach(function(file) {
    if (file === 'node_modules' || file === 'dist' || file.startsWith('.')) return;
    const fullPath = dirPath + "/" + file;
    if (fs.statSync(fullPath).isDirectory()) {
      arrayOfFiles = getAllFiles(fullPath, arrayOfFiles);
    } else {
      if ((fullPath.endsWith('.ts') || fullPath.endsWith('.tsx')) && !fullPath.includes('.test.')) {
          arrayOfFiles.push(fullPath);
      }
    }
  });

  return arrayOfFiles;
}

const allCodeFiles = getAllFiles('../FinancialApp/src');

for (const table of tables) {
    let found = false;
    for (const file of allCodeFiles) {
        const content = fs.readFileSync(file, 'utf8');
        // Check for the model name or related terms
        if (content.toLowerCase().includes(table.toLowerCase())) {
            found = true;
            break;
        }
    }
    if (!found) {
        console.log(`UNUSED MODEL (Frontend): ${table}`);
    }
}
