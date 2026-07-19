using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class MakeDarkModeNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "DarkMode",
                table: "FinancialSettings",
                type: "boolean",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);

            // Existing rows were auto-defaulted to false (light). Treat those as "never chosen"
            // so the client falls back to the OS/browser color scheme instead of forcing light.
            // Explicit dark (true) preferences are preserved.
            migrationBuilder.Sql("UPDATE \"FinancialSettings\" SET \"DarkMode\" = NULL WHERE \"DarkMode\" = false;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "DarkMode",
                table: "FinancialSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);
        }
    }
}
