using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260709042000_StoreTransactionTimestamp")]
    public partial class StoreTransactionTimestamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "Transactions"
                ALTER COLUMN "Date" TYPE timestamp with time zone
                USING ("Date"::timestamp AT TIME ZONE 'UTC');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "Transactions"
                ALTER COLUMN "Date" TYPE date
                USING ("Date" AT TIME ZONE 'UTC')::date;
                """);
        }
    }
}
