using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiUserOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WishlistItems_ClientKey",
                table: "WishlistItems");

            migrationBuilder.DropIndex(
                name: "IX_WishlistItems_PurchaseTransactionId",
                table: "WishlistItems");

            migrationBuilder.DropIndex(
                name: "IX_WebAuthnCredentials_Username",
                table: "WebAuthnCredentials");

            migrationBuilder.DropIndex(
                name: "IX_UserSessions_Username_DeviceId",
                table: "UserSessions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_Date_PostedAt_LedgerCategory",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_WishlistItemId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_RecoveryCodes_Username",
                table: "RecoveryCodes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_CycleBalances",
                table: "CycleBalances");

            migrationBuilder.DropIndex(
                name: "IX_AppUsers_SingletonKey",
                table: "AppUsers");

            migrationBuilder.RenameColumn(
                name: "SingletonKey",
                table: "AppUsers",
                newName: "RegistrationSlot");

            migrationBuilder.AlterColumn<int>(
                name: "RegistrationSlot",
                table: "AppUsers",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "WishlistItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "WebAuthnCredentials",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "WebAuthnChallenges",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "UserSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "Transactions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "TransactionCategories",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "RecurringPayments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "RecoveryCodes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "ReceiptScanJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "PendingTwoFactors",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "FinancialSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "CycleBalances",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedUsername",
                table: "AppUsers",
                type: "text",
                nullable: true);

            // The deployed application has at most one AppUser (enforced by the old
            // SingletonKey index), so every existing financial row belongs to that account.
            // Authentication rows are matched by username to make the ownership explicit.
            // If an installation has never registered, only unowned seed/default rows can
            // exist; remove those so the required foreign keys can be added safely.
            migrationBuilder.Sql("""
                UPDATE "AppUsers"
                SET "NormalizedUsername" = UPPER(BTRIM("Username"));

                UPDATE "UserSessions" AS child SET "UserId" = owner."Id"
                FROM "AppUsers" AS owner
                WHERE LOWER(child."Username") = LOWER(owner."Username");
                UPDATE "WebAuthnCredentials" AS child SET "UserId" = owner."Id"
                FROM "AppUsers" AS owner
                WHERE LOWER(child."Username") = LOWER(owner."Username");
                UPDATE "WebAuthnChallenges" AS child SET "UserId" = owner."Id"
                FROM "AppUsers" AS owner
                WHERE LOWER(child."Username") = LOWER(owner."Username");
                UPDATE "PendingTwoFactors" AS child SET "UserId" = owner."Id"
                FROM "AppUsers" AS owner
                WHERE LOWER(child."Username") = LOWER(owner."Username");
                UPDATE "RecoveryCodes" AS child SET "UserId" = owner."Id"
                FROM "AppUsers" AS owner
                WHERE LOWER(child."Username") = LOWER(owner."Username");
                UPDATE "ReceiptScanJobs" AS child SET "UserId" = owner."Id"
                FROM "AppUsers" AS owner
                WHERE LOWER(child."Username") = LOWER(owner."Username");

                UPDATE "Transactions" SET "UserId" = (SELECT "Id" FROM "AppUsers" LIMIT 1);
                UPDATE "RecurringPayments" SET "UserId" = (SELECT "Id" FROM "AppUsers" LIMIT 1);
                UPDATE "FinancialSettings" SET "UserId" = (SELECT "Id" FROM "AppUsers" LIMIT 1);
                UPDATE "TransactionCategories" SET "UserId" = (SELECT "Id" FROM "AppUsers" LIMIT 1);
                UPDATE "WishlistItems" SET "UserId" = (SELECT "Id" FROM "AppUsers" LIMIT 1);
                UPDATE "CycleBalances" SET "UserId" = (SELECT "Id" FROM "AppUsers" LIMIT 1);

                DELETE FROM "UserSessions" WHERE "UserId" IS NULL;
                DELETE FROM "WebAuthnCredentials" WHERE "UserId" IS NULL;
                DELETE FROM "WebAuthnChallenges" WHERE "UserId" IS NULL;
                DELETE FROM "PendingTwoFactors" WHERE "UserId" IS NULL;
                DELETE FROM "RecoveryCodes" WHERE "UserId" IS NULL;
                DELETE FROM "ReceiptScanJobs" WHERE "UserId" IS NULL;
                DELETE FROM "Transactions" WHERE "UserId" IS NULL;
                DELETE FROM "RecurringPayments" WHERE "UserId" IS NULL;
                DELETE FROM "FinancialSettings" WHERE "UserId" IS NULL;
                DELETE FROM "TransactionCategories" WHERE "UserId" IS NULL;
                DELETE FROM "WishlistItems" WHERE "UserId" IS NULL;
                DELETE FROM "CycleBalances" WHERE "UserId" IS NULL;

                ALTER TABLE "WishlistItems" ALTER COLUMN "UserId" SET NOT NULL;
                ALTER TABLE "WebAuthnCredentials" ALTER COLUMN "UserId" SET NOT NULL;
                ALTER TABLE "WebAuthnChallenges" ALTER COLUMN "UserId" SET NOT NULL;
                ALTER TABLE "UserSessions" ALTER COLUMN "UserId" SET NOT NULL;
                ALTER TABLE "Transactions" ALTER COLUMN "UserId" SET NOT NULL;
                ALTER TABLE "TransactionCategories" ALTER COLUMN "UserId" SET NOT NULL;
                ALTER TABLE "RecurringPayments" ALTER COLUMN "UserId" SET NOT NULL;
                ALTER TABLE "RecoveryCodes" ALTER COLUMN "UserId" SET NOT NULL;
                ALTER TABLE "ReceiptScanJobs" ALTER COLUMN "UserId" SET NOT NULL;
                ALTER TABLE "PendingTwoFactors" ALTER COLUMN "UserId" SET NOT NULL;
                ALTER TABLE "FinancialSettings" ALTER COLUMN "UserId" SET NOT NULL;
                ALTER TABLE "CycleBalances" ALTER COLUMN "UserId" SET NOT NULL;
                ALTER TABLE "AppUsers" ALTER COLUMN "NormalizedUsername" SET NOT NULL;
                """);

            migrationBuilder.AddPrimaryKey(
                name: "PK_CycleBalances",
                table: "CycleBalances",
                columns: new[] { "UserId", "Year", "MonthIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_UserId_ClientKey",
                table: "WishlistItems",
                columns: new[] { "UserId", "ClientKey" },
                unique: true,
                filter: "\"ClientKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_UserId_PurchaseTransactionId",
                table: "WishlistItems",
                columns: new[] { "UserId", "PurchaseTransactionId" });

            migrationBuilder.CreateIndex(
                name: "IX_WebAuthnCredentials_UserId",
                table: "WebAuthnCredentials",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_WebAuthnChallenges_UserId",
                table: "WebAuthnChallenges",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_UserId_DeviceId",
                table: "UserSessions",
                columns: new[] { "UserId", "DeviceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_UserId_Date_PostedAt_LedgerCategory",
                table: "Transactions",
                columns: new[] { "UserId", "Date", "PostedAt", "LedgerCategory" },
                descending: new[] { false, true, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_UserId_WishlistItemId",
                table: "Transactions",
                columns: new[] { "UserId", "WishlistItemId" },
                unique: true,
                filter: "\"WishlistItemId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionCategories_UserId_Name",
                table: "TransactionCategories",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecurringPayments_UserId",
                table: "RecurringPayments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryCodes_UserId",
                table: "RecoveryCodes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptScanJobs_UserId_Status_UpdatedAt",
                table: "ReceiptScanJobs",
                columns: new[] { "UserId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingTwoFactors_UserId",
                table: "PendingTwoFactors",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialSettings_UserId",
                table: "FinancialSettings",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_NormalizedUsername",
                table: "AppUsers",
                column: "NormalizedUsername",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_RegistrationSlot",
                table: "AppUsers",
                column: "RegistrationSlot",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CycleBalances_AppUsers_UserId",
                table: "CycleBalances",
                column: "UserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_FinancialSettings_AppUsers_UserId",
                table: "FinancialSettings",
                column: "UserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PendingTwoFactors_AppUsers_UserId",
                table: "PendingTwoFactors",
                column: "UserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ReceiptScanJobs_AppUsers_UserId",
                table: "ReceiptScanJobs",
                column: "UserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RecoveryCodes_AppUsers_UserId",
                table: "RecoveryCodes",
                column: "UserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RecurringPayments_AppUsers_UserId",
                table: "RecurringPayments",
                column: "UserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TransactionCategories_AppUsers_UserId",
                table: "TransactionCategories",
                column: "UserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Transactions_AppUsers_UserId",
                table: "Transactions",
                column: "UserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_UserSessions_AppUsers_UserId",
                table: "UserSessions",
                column: "UserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_WebAuthnChallenges_AppUsers_UserId",
                table: "WebAuthnChallenges",
                column: "UserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_WebAuthnCredentials_AppUsers_UserId",
                table: "WebAuthnCredentials",
                column: "UserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_WishlistItems_AppUsers_UserId",
                table: "WishlistItems",
                column: "UserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CycleBalances_AppUsers_UserId",
                table: "CycleBalances");

            migrationBuilder.DropForeignKey(
                name: "FK_FinancialSettings_AppUsers_UserId",
                table: "FinancialSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_PendingTwoFactors_AppUsers_UserId",
                table: "PendingTwoFactors");

            migrationBuilder.DropForeignKey(
                name: "FK_ReceiptScanJobs_AppUsers_UserId",
                table: "ReceiptScanJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_RecoveryCodes_AppUsers_UserId",
                table: "RecoveryCodes");

            migrationBuilder.DropForeignKey(
                name: "FK_RecurringPayments_AppUsers_UserId",
                table: "RecurringPayments");

            migrationBuilder.DropForeignKey(
                name: "FK_TransactionCategories_AppUsers_UserId",
                table: "TransactionCategories");

            migrationBuilder.DropForeignKey(
                name: "FK_Transactions_AppUsers_UserId",
                table: "Transactions");

            migrationBuilder.DropForeignKey(
                name: "FK_UserSessions_AppUsers_UserId",
                table: "UserSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_WebAuthnChallenges_AppUsers_UserId",
                table: "WebAuthnChallenges");

            migrationBuilder.DropForeignKey(
                name: "FK_WebAuthnCredentials_AppUsers_UserId",
                table: "WebAuthnCredentials");

            migrationBuilder.DropForeignKey(
                name: "FK_WishlistItems_AppUsers_UserId",
                table: "WishlistItems");

            migrationBuilder.DropIndex(
                name: "IX_WishlistItems_UserId_ClientKey",
                table: "WishlistItems");

            migrationBuilder.DropIndex(
                name: "IX_WishlistItems_UserId_PurchaseTransactionId",
                table: "WishlistItems");

            migrationBuilder.DropIndex(
                name: "IX_WebAuthnCredentials_UserId",
                table: "WebAuthnCredentials");

            migrationBuilder.DropIndex(
                name: "IX_WebAuthnChallenges_UserId",
                table: "WebAuthnChallenges");

            migrationBuilder.DropIndex(
                name: "IX_UserSessions_UserId_DeviceId",
                table: "UserSessions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_UserId_Date_PostedAt_LedgerCategory",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_UserId_WishlistItemId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_TransactionCategories_UserId_Name",
                table: "TransactionCategories");

            migrationBuilder.DropIndex(
                name: "IX_RecurringPayments_UserId",
                table: "RecurringPayments");

            migrationBuilder.DropIndex(
                name: "IX_RecoveryCodes_UserId",
                table: "RecoveryCodes");

            migrationBuilder.DropIndex(
                name: "IX_ReceiptScanJobs_UserId_Status_UpdatedAt",
                table: "ReceiptScanJobs");

            migrationBuilder.DropIndex(
                name: "IX_PendingTwoFactors_UserId",
                table: "PendingTwoFactors");

            migrationBuilder.DropIndex(
                name: "IX_FinancialSettings_UserId",
                table: "FinancialSettings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_CycleBalances",
                table: "CycleBalances");

            migrationBuilder.DropIndex(
                name: "IX_AppUsers_NormalizedUsername",
                table: "AppUsers");

            migrationBuilder.DropIndex(
                name: "IX_AppUsers_RegistrationSlot",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "WishlistItems");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "WebAuthnCredentials");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "WebAuthnChallenges");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "TransactionCategories");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "RecurringPayments");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "RecoveryCodes");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "ReceiptScanJobs");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "PendingTwoFactors");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "FinancialSettings");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "CycleBalances");

            migrationBuilder.DropColumn(
                name: "NormalizedUsername",
                table: "AppUsers");

            migrationBuilder.AlterColumn<int>(
                name: "RegistrationSlot",
                table: "AppUsers",
                type: "integer",
                nullable: false,
                defaultValue: 1,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.RenameColumn(
                name: "RegistrationSlot",
                table: "AppUsers",
                newName: "SingletonKey");

            migrationBuilder.AddPrimaryKey(
                name: "PK_CycleBalances",
                table: "CycleBalances",
                columns: new[] { "Year", "MonthIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_ClientKey",
                table: "WishlistItems",
                column: "ClientKey",
                unique: true,
                filter: "\"ClientKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_PurchaseTransactionId",
                table: "WishlistItems",
                column: "PurchaseTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_WebAuthnCredentials_Username",
                table: "WebAuthnCredentials",
                column: "Username");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_Username_DeviceId",
                table: "UserSessions",
                columns: new[] { "Username", "DeviceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_Date_PostedAt_LedgerCategory",
                table: "Transactions",
                columns: new[] { "Date", "PostedAt", "LedgerCategory" },
                descending: new[] { true, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_WishlistItemId",
                table: "Transactions",
                column: "WishlistItemId",
                unique: true,
                filter: "\"WishlistItemId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryCodes_Username",
                table: "RecoveryCodes",
                column: "Username");

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_SingletonKey",
                table: "AppUsers",
                column: "SingletonKey",
                unique: true);
        }
    }
}
