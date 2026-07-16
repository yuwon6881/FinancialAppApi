using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class HardenOcrJobLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReceiptScanJobs_Username_Status_CreatedAt",
                table: "ReceiptScanJobs");

            migrationBuilder.AddColumn<byte[]>(
                name: "ImageData",
                table: "ReceiptScanJobs",
                type: "bytea",
                nullable: true);

            // Preserve any queued receipt that exists during deployment while moving from
            // base64 text to compact binary storage. All application-written values are valid
            // base64; empty/null payloads remain null.
            migrationBuilder.Sql("""
                UPDATE "ReceiptScanJobs"
                SET "ImageData" = decode("ImageBase64", 'base64')
                WHERE "ImageBase64" IS NOT NULL AND "ImageBase64" <> '';
                """);

            migrationBuilder.DropColumn(
                name: "ImageBase64",
                table: "ReceiptScanJobs");

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptScanJobs_Status_UpdatedAt",
                table: "ReceiptScanJobs",
                columns: new[] { "Status", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReceiptScanJobs_Status_UpdatedAt",
                table: "ReceiptScanJobs");

            migrationBuilder.AddColumn<string>(
                name: "ImageBase64",
                table: "ReceiptScanJobs",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "ReceiptScanJobs"
                SET "ImageBase64" = encode("ImageData", 'base64')
                WHERE "ImageData" IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "ImageData",
                table: "ReceiptScanJobs");

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptScanJobs_Username_Status_CreatedAt",
                table: "ReceiptScanJobs",
                columns: new[] { "Username", "Status", "CreatedAt" });
        }
    }
}
