using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPushSubscriptionPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "PushSubscriptions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "web");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pushsubscriptions_platform",
                table: "PushSubscriptions",
                sql: "\"Platform\" IN ('web', 'android', 'ios')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_pushsubscriptions_platform",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "PushSubscriptions");
        }
    }
}
