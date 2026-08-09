using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaDuplicateCleanupRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "media_duplicate_cleanup_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DuplicateGroupCount = table.Column<int>(type: "integer", nullable: false),
                    FilesRemovedCount = table.Column<int>(type: "integer", nullable: false),
                    FilesRetainedCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorSummary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_duplicate_cleanup_runs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_media_duplicate_cleanup_runs_job",
                table: "media_duplicate_cleanup_runs",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "ix_media_duplicate_cleanup_runs_owner_created",
                table: "media_duplicate_cleanup_runs",
                columns: new[] { "OwnerUserId", "CreatedAt" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_duplicate_cleanup_runs");
        }
    }
}
