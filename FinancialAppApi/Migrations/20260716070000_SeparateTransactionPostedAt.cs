using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260716070000_SeparateTransactionPostedAt")]
public partial class SeparateTransactionPostedAt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "PostedAt",
            table: "Transactions",
            type: "timestamp with time zone",
            nullable: true);

        // Modern PWA transaction ids contain the client-side Unix millisecond timestamp.
        // Recover it for existing rows; legacy/generated ids fall back to their old Date value.
        migrationBuilder.Sql("""
            UPDATE "Transactions"
            SET "PostedAt" = CASE
                WHEN substring("Id" from '^tx-([0-9]{13})') IS NOT NULL
                    THEN to_timestamp(substring("Id" from '^tx-([0-9]{13})')::double precision / 1000.0)
                ELSE "Date"
            END;

            UPDATE "Transactions"
            SET "Date" = (("Date" AT TIME ZONE 'UTC')::date::timestamp AT TIME ZONE 'UTC');
            """);

        migrationBuilder.AlterColumn<DateTime>(
            name: "PostedAt",
            table: "Transactions",
            type: "timestamp with time zone",
            nullable: false,
            oldClrType: typeof(DateTime),
            oldType: "timestamp with time zone",
            oldNullable: true);

        migrationBuilder.DropIndex(
            name: "IX_Transactions_Date_LedgerCategory",
            table: "Transactions");

        migrationBuilder.CreateIndex(
            name: "IX_Transactions_Date_PostedAt_LedgerCategory",
            table: "Transactions",
            columns: new[] { "Date", "PostedAt", "LedgerCategory" },
            descending: new[] { true, true, false });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Transactions_Date_PostedAt_LedgerCategory",
            table: "Transactions");

        migrationBuilder.CreateIndex(
            name: "IX_Transactions_Date_LedgerCategory",
            table: "Transactions",
            columns: new[] { "Date", "LedgerCategory" },
            descending: new[] { true, false });

        migrationBuilder.DropColumn(
            name: "PostedAt",
            table: "Transactions");
    }
}
