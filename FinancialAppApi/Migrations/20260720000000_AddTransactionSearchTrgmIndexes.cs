using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using FinancialAppApi.Database;

#nullable disable

namespace FinancialAppApi.Migrations;

// Trigram GIN indexes backing the case-insensitive ledger search (ILIKE '%term%').
// These are raw-SQL, extension-dependent indexes that are not represented in the EF
// model, so — like the other hand-authored migrations in this folder — there is no
// matching ModelSnapshot change. They only ever run against PostgreSQL.
[DbContext(typeof(AppDbContext))]
[Migration("20260720000000_AddTransactionSearchTrgmIndexes")]
public partial class AddTransactionSearchTrgmIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
        migrationBuilder.Sql(
            "CREATE INDEX IF NOT EXISTS \"IX_Transactions_Description_trgm\" " +
            "ON \"Transactions\" USING gin (\"Description\" gin_trgm_ops);");
        migrationBuilder.Sql(
            "CREATE INDEX IF NOT EXISTS \"IX_Transactions_Category_trgm\" " +
            "ON \"Transactions\" USING gin (\"Category\" gin_trgm_ops);");
        migrationBuilder.Sql(
            "CREATE INDEX IF NOT EXISTS \"IX_Transactions_LedgerCategory_trgm\" " +
            "ON \"Transactions\" USING gin (\"LedgerCategory\" gin_trgm_ops);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Transactions_Description_trgm\";");
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Transactions_Category_trgm\";");
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Transactions_LedgerCategory_trgm\";");
        // The pg_trgm extension is left installed: other objects may depend on it and it is
        // harmless to keep.
    }
}
