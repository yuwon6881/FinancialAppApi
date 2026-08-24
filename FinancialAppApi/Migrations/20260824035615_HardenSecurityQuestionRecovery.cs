using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class HardenSecurityQuestionRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SecurityQuestionRecoveryFailedAttempts",
                table: "AppUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "SecurityQuestionRecoveryLockedUntil",
                table: "AppUsers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecurityQuestionRecoveryFailedAttempts",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "SecurityQuestionRecoveryLockedUntil",
                table: "AppUsers");
        }
    }
}
