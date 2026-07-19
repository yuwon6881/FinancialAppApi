using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations;

/// <summary>
/// Contracts the OCR job schema after receipt images moved to Supabase Storage.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260719143000_DropLegacyReceiptImageData")]
public partial class DropLegacyReceiptImageData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE "ReceiptScanJobs"
            SET "Status" = 'failed',
                "ErrorMessage" = 'This receipt scan expired during a storage upgrade. Please submit it again.',
                "CompletedAt" = NOW(),
                "UpdatedAt" = NOW()
            WHERE "ImageData" IS NOT NULL
              AND "StorageObjectPath" IS NULL
              AND "Status" NOT IN ('completed', 'failed');
            """);

        migrationBuilder.DropColumn(
            name: "ImageData",
            table: "ReceiptScanJobs");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<byte[]>(
            name: "ImageData",
            table: "ReceiptScanJobs",
            type: "bytea",
            nullable: true);
    }
}
