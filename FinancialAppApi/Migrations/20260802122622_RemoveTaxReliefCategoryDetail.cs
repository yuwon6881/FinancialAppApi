using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTaxReliefCategoryDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Detail",
                table: "TaxReliefCategoryLimits");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Detail",
                table: "TaxReliefCategoryLimits",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");
        }
    }
}
