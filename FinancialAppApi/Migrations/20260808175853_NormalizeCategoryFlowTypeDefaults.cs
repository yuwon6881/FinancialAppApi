using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeCategoryFlowTypeDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "TransactionCategories"
                SET "Type" = CASE lower(trim("Type"))
                    WHEN 'inflow' THEN 'inflow'
                    WHEN 'outflow' THEN 'outflow'
                    WHEN 'both' THEN 'both'
                    ELSE 'both'
                END
                WHERE "Type" IS NULL OR lower(trim("Type")) NOT IN ('inflow', 'outflow', 'both');
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "TransactionCategories",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "both",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "TransactionCategories",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldDefaultValue: "both");
        }
    }
}
