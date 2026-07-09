using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260709035000_EnsureWishlistPurchaseLinkColumns")]
    public partial class EnsureWishlistPurchaseLinkColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "Transactions"
                ADD COLUMN IF NOT EXISTS "WishlistItemId" integer;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "WishlistItems"
                ADD COLUMN IF NOT EXISTS "PurchaseTransactionId" text;
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Transactions_WishlistItemId"
                ON "Transactions" ("WishlistItemId");
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_WishlistItems_PurchaseTransactionId"
                ON "WishlistItems" ("PurchaseTransactionId");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_Transactions_WishlistItemId";
                """);

            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_WishlistItems_PurchaseTransactionId";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "Transactions"
                DROP COLUMN IF EXISTS "WishlistItemId";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "WishlistItems"
                DROP COLUMN IF EXISTS "PurchaseTransactionId";
                """);
        }
    }
}
