using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminImportItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConflictSamplesJson",
                table: "admin_import_runs");

            migrationBuilder.AddColumn<int>(
                name: "CancelledFiles",
                table: "admin_import_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Phase",
                table: "admin_import_runs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScanCompletedAt",
                table: "admin_import_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "admin_import_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RelativePath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    SourceModifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FileItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    FailureCategory = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ConflictCategory = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_import_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_admin_import_items_admin_import_runs_ImportRunId",
                        column: x => x.ImportRunId,
                        principalTable: "admin_import_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_import_items_run_status",
                table: "admin_import_items",
                columns: new[] { "ImportRunId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ux_admin_import_items_run_ordinal",
                table: "admin_import_items",
                columns: new[] { "ImportRunId", "Ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_import_items");

            migrationBuilder.DropColumn(
                name: "CancelledFiles",
                table: "admin_import_runs");

            migrationBuilder.DropColumn(
                name: "Phase",
                table: "admin_import_runs");

            migrationBuilder.DropColumn(
                name: "ScanCompletedAt",
                table: "admin_import_runs");

            migrationBuilder.AddColumn<string>(
                name: "ConflictSamplesJson",
                table: "admin_import_runs",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);
        }
    }
}
