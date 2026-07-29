using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialAppApi.Migrations
{
    /// <inheritdoc />
    public partial class AddVaultDocumentTypesAndPagingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VaultDocuments_UserId_TaxYear",
                table: "VaultDocuments");

            migrationBuilder.CreateTable(
                name: "VaultDocumentTypes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultDocumentTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VaultDocumentTypes_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO "VaultDocumentTypes" ("Id", "UserId", "Name")
                SELECT 'doctype-' || substr(md5(u."Id" || defaults."Name"), 1, 32), u."Id", defaults."Name"
                FROM "AppUsers" AS u
                CROSS JOIN (VALUES
                    ('Receipt'), ('Invoice'), ('Tax Return'), ('Bank Statement'),
                    ('Donation Certificate'), ('Medical Bill'), ('Insurance Policy'), ('Other')
                ) AS defaults("Name")
                ON CONFLICT ("Id") DO NOTHING;

                INSERT INTO "VaultDocumentTypes" ("Id", "UserId", "Name")
                SELECT 'doctype-' || substr(md5(d."UserId" || d."DocumentType"), 1, 32),
                       d."UserId",
                       d."DocumentType"
                FROM "VaultDocuments" AS d
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM "VaultDocumentTypes" AS existing
                    WHERE existing."UserId" = d."UserId"
                      AND lower(existing."Name") = lower(d."DocumentType")
                )
                GROUP BY d."UserId", d."DocumentType"
                ON CONFLICT ("Id") DO NOTHING;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_VaultDocuments_UserId_TaxYear_UploadedAt_Id",
                table: "VaultDocuments",
                columns: new[] { "UserId", "TaxYear", "UploadedAt", "Id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_VaultDocuments_UserId_UploadedAt_Id",
                table: "VaultDocuments",
                columns: new[] { "UserId", "UploadedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_VaultDocumentTypes_UserId_Name",
                table: "VaultDocumentTypes",
                columns: new[] { "UserId", "Name" },
                unique: true);
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_VaultDocumentTypes_UserId_NormalizedName\" " +
                "ON \"VaultDocumentTypes\" (\"UserId\", lower(\"Name\"));");

            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_VaultDocuments_OriginalFileName_trgm\" " +
                "ON \"VaultDocuments\" USING gin (lower(\"OriginalFileName\") gin_trgm_ops);");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_VaultDocuments_Notes_trgm\" " +
                "ON \"VaultDocuments\" USING gin (lower(\"Notes\") gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_VaultDocuments_OriginalFileName_trgm\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_VaultDocuments_Notes_trgm\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_VaultDocumentTypes_UserId_NormalizedName\";");

            migrationBuilder.DropTable(
                name: "VaultDocumentTypes");

            migrationBuilder.DropIndex(
                name: "IX_VaultDocuments_UserId_TaxYear_UploadedAt_Id",
                table: "VaultDocuments");

            migrationBuilder.DropIndex(
                name: "IX_VaultDocuments_UserId_UploadedAt_Id",
                table: "VaultDocuments");

            migrationBuilder.CreateIndex(
                name: "IX_VaultDocuments_UserId_TaxYear",
                table: "VaultDocuments",
                columns: new[] { "UserId", "TaxYear" });
        }
    }
}
