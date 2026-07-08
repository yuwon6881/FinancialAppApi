using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityHardeningAndTwoFactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "Id",
                table: "UserSessions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "IpAddress",
                table: "UserSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastActiveAt",
                table: "UserSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserAgent",
                table: "UserSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "AppUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailVerified",
                table: "AppUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "FailedLoginAttempts",
                table: "AppUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockedUntil",
                table: "AppUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingTotpSecret",
                table: "AppUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TotpEnabled",
                table: "AppUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TotpSecret",
                table: "AppUsers",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EmailVerificationCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    CodeHash = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailVerificationCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PendingTwoFactors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    DeviceId = table.Column<string>(type: "text", nullable: true),
                    DeviceName = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingTwoFactors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecoveryCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    CodeHash = table.Column<string>(type: "text", nullable: false),
                    Used = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryCodes", x => x.Id);
                });

            // Existing rows all got the same zero-value default above; give each a distinct id
            // before the unique index below is created, or that index creation would fail.
            migrationBuilder.Sql(@"UPDATE ""UserSessions"" SET ""Id"" = gen_random_uuid();");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_Id",
                table: "UserSessions",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailVerificationCodes_Username",
                table: "EmailVerificationCodes",
                column: "Username");

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryCodes_Username",
                table: "RecoveryCodes",
                column: "Username");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailVerificationCodes");

            migrationBuilder.DropTable(
                name: "PendingTwoFactors");

            migrationBuilder.DropTable(
                name: "RecoveryCodes");

            migrationBuilder.DropIndex(
                name: "IX_UserSessions_Id",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "IpAddress",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "LastActiveAt",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "UserAgent",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "EmailVerified",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "FailedLoginAttempts",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "LockedUntil",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "PendingTotpSecret",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "TotpEnabled",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "TotpSecret",
                table: "AppUsers");
        }
    }
}
