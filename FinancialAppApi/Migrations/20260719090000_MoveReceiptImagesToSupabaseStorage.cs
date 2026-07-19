using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations;

/// <summary>
/// Expands the OCR job schema with an external object path. The following contract
/// migration removes ImageData after legacy queued jobs are marked for resubmission.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260719090000_MoveReceiptImagesToSupabaseStorage")]
public partial class MoveReceiptImagesToSupabaseStorage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "StorageObjectPath",
            table: "ReceiptScanJobs",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "StorageObjectPath",
            table: "ReceiptScanJobs");
    }
}
