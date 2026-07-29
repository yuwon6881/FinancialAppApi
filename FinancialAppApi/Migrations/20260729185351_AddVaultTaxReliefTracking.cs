using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddVaultTaxReliefTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Amount",
                table: "VaultDocuments",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AmountConfidence",
                table: "VaultDocuments",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AmountCurrency",
                table: "VaultDocuments",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "MYR");

            migrationBuilder.AddColumn<string>(
                name: "AmountExtractionMessage",
                table: "VaultDocuments",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AmountStatus",
                table: "VaultDocuments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unavailable");

            migrationBuilder.AddColumn<string>(
                name: "ReliefCategory",
                table: "VaultDocuments",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Amount",
                table: "VaultDocuments");

            migrationBuilder.DropColumn(
                name: "AmountConfidence",
                table: "VaultDocuments");

            migrationBuilder.DropColumn(
                name: "AmountCurrency",
                table: "VaultDocuments");

            migrationBuilder.DropColumn(
                name: "AmountExtractionMessage",
                table: "VaultDocuments");

            migrationBuilder.DropColumn(
                name: "AmountStatus",
                table: "VaultDocuments");

            migrationBuilder.DropColumn(
                name: "ReliefCategory",
                table: "VaultDocuments");
        }
    }
}
