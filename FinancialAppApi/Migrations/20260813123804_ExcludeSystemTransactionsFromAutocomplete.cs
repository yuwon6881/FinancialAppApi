using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class ExcludeSystemTransactionsFromAutocomplete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExcludeFromAutocomplete",
                table: "Transactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // This is the only text-based step. It preserves the legacy wishlist rows that were
            // written before structural links existed; runtime autocomplete uses the stored flag.
            migrationBuilder.Sql("""
                UPDATE "Transactions"
                SET "ExcludeFromAutocomplete" = TRUE
                WHERE lower(coalesce("Category", '')) = 'adjustment'
                   OR lower(coalesce("Category", '')) = 'transfer'
                   OR lower(coalesce("LedgerCategory", '')) = 'accountmove'
                   OR lower(coalesce("LedgerCategory", '')) LIKE 'transfer:%'
                   OR lower(coalesce("LedgerCategory", '')) = 'discarded'
                   OR "Id" ILIKE '%-split-%'
                   OR "WishlistItemId" IS NOT NULL
                   OR "SavingsGoalId" IS NOT NULL
                   OR "Description" ILIKE 'Purchased:%'
                   OR "Description" ILIKE '%(Wish List)';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExcludeFromAutocomplete",
                table: "Transactions");
        }
    }
}
