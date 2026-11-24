using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class ConfigureOptionalInvestmentGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create a plan for each existing account once, including accounts that previously
            // used the implicit targets. Existing custom groups and allocations stay intact.
            migrationBuilder.Sql("""
                INSERT INTO "InvestmentPlans" ("Id", "UserId", "UsEquityTarget", "InternationalExUsTarget", "BondsTarget", "WatchDrift", "AlertDrift", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), "Id", 66, 10, 24, 3, 5, now(), now()
                FROM "AppUsers"
                ON CONFLICT ("UserId") DO NOTHING;

                UPDATE "InvestmentPlans"
                SET "GroupsJson" = jsonb_build_array(
                    jsonb_build_object('Key', 'USEquity', 'Label', 'US Equity', 'TargetPercentage', "UsEquityTarget", 'ForecastClass', 'USEquity'),
                    jsonb_build_object('Key', 'InternationalExUS', 'Label', 'International ex-US', 'TargetPercentage', "InternationalExUsTarget", 'ForecastClass', 'InternationalExUS'),
                    jsonb_build_object('Key', 'Bonds', 'Label', 'Bonds', 'TargetPercentage', "BondsTarget", 'ForecastClass', 'Bonds'))
                WHERE "GroupsJson" IS NULL;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "ck_investmentplans_targets_positive",
                table: "InvestmentPlans");

            migrationBuilder.DropCheckConstraint(
                name: "ck_investmentplans_targets_total",
                table: "InvestmentPlans");

            migrationBuilder.DropCheckConstraint(
                name: "ck_investmentinstruments_allocationsleeve",
                table: "InvestmentInstruments");

            migrationBuilder.DropColumn(
                name: "BondsTarget",
                table: "InvestmentPlans");

            migrationBuilder.DropColumn(
                name: "InternationalExUsTarget",
                table: "InvestmentPlans");

            migrationBuilder.DropColumn(
                name: "UsEquityTarget",
                table: "InvestmentPlans");

            migrationBuilder.AlterColumn<string>(
                name: "GroupsJson",
                table: "InvestmentPlans",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GroupingEnabled",
                table: "InvestmentPlans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TargetsEnabled",
                table: "InvestmentPlans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE "InvestmentPlans" SET "GroupingEnabled" = true, "TargetsEnabled" = true;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_investmentplans_groups_array",
                table: "InvestmentPlans",
                sql: "jsonb_typeof(\"GroupsJson\") = 'array'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_investmentinstruments_allocationsleeve",
                table: "InvestmentInstruments",
                sql: "\"AllocationSleeve\" IS NULL OR (length(\"AllocationSleeve\") BETWEEN 1 AND 32 AND ltrim(\"AllocationSleeve\", '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz') = '')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_investmentplans_groups_array",
                table: "InvestmentPlans");

            migrationBuilder.DropCheckConstraint(
                name: "ck_investmentinstruments_allocationsleeve",
                table: "InvestmentInstruments");

            migrationBuilder.DropColumn(
                name: "GroupingEnabled",
                table: "InvestmentPlans");

            migrationBuilder.DropColumn(
                name: "TargetsEnabled",
                table: "InvestmentPlans");

            migrationBuilder.AlterColumn<string>(
                name: "GroupsJson",
                table: "InvestmentPlans",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.AddColumn<decimal>(
                name: "BondsTarget",
                table: "InvestmentPlans",
                type: "numeric(5,2)",
                nullable: false,
                defaultValue: 24m);

            migrationBuilder.AddColumn<decimal>(
                name: "InternationalExUsTarget",
                table: "InvestmentPlans",
                type: "numeric(5,2)",
                nullable: false,
                defaultValue: 10m);

            migrationBuilder.AddColumn<decimal>(
                name: "UsEquityTarget",
                table: "InvestmentPlans",
                type: "numeric(5,2)",
                nullable: false,
                defaultValue: 66m);

            migrationBuilder.Sql("""
                UPDATE "InvestmentPlans" SET "GroupsJson" = NULL WHERE "GroupsJson" = '[]'::jsonb;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_investmentplans_targets_positive",
                table: "InvestmentPlans",
                sql: "\"UsEquityTarget\" > 0 AND \"InternationalExUsTarget\" > 0 AND \"BondsTarget\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_investmentplans_targets_total",
                table: "InvestmentPlans",
                sql: "\"UsEquityTarget\" + \"InternationalExUsTarget\" + \"BondsTarget\" = 100");

            migrationBuilder.AddCheckConstraint(
                name: "ck_investmentinstruments_allocationsleeve",
                table: "InvestmentInstruments",
                sql: "\"AllocationSleeve\" IS NULL OR \"AllocationSleeve\" IN ('USEquity', 'InternationalExUS', 'Bonds') OR (length(\"AllocationSleeve\") = 32 AND ltrim(\"AllocationSleeve\", '0123456789abcdef') = '')");
        }
    }
}
