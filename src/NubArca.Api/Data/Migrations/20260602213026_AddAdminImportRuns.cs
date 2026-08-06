using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminImportRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_import_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AdminUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationFolderId = table.Column<Guid>(type: "uuid", nullable: true),
                    RootId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceRelativePath = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScannedFiles = table.Column<int>(type: "integer", nullable: false),
                    ImportedFiles = table.Column<int>(type: "integer", nullable: false),
                    SkippedFiles = table.Column<int>(type: "integer", nullable: false),
                    FailedFiles = table.Column<int>(type: "integer", nullable: false),
                    ConflictFiles = table.Column<int>(type: "integer", nullable: false),
                    ImportedBytes = table.Column<long>(type: "bigint", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    TotalDirectories = table.Column<int>(type: "integer", nullable: false),
                    CurrentRelativePath = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    ErrorSummary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_import_runs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_import_runs_target_user",
                table: "admin_import_runs",
                column: "TargetUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_import_runs");
        }
    }
}
