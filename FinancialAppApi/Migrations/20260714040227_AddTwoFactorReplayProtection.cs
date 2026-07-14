
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddTwoFactorReplayProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastTotpTimeStep",
                table: "AppUsers",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TwoFactorFailedAttempts",
                table: "AppUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "TwoFactorLockedUntil",
                table: "AppUsers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastTotpTimeStep",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "TwoFactorFailedAttempts",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "TwoFactorLockedUntil",
                table: "AppUsers");
        }
    }
}
