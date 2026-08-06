using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoOrganizerRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "photo_organizer_moves",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceParentFolderId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TargetParentFolderId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    EffectiveDateTaken = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DateTakenSource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_photo_organizer_moves", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "photo_organizer_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OptionsJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    DryRunSummaryJson = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CandidateCount = table.Column<int>(type: "integer", nullable: false),
                    MovedCount = table.Column<int>(type: "integer", nullable: false),
                    AlreadyOrganizedCount = table.Column<int>(type: "integer", nullable: false),
                    SkippedMissingDateCount = table.Column<int>(type: "integer", nullable: false),
                    SkippedConflictCount = table.Column<int>(type: "integer", nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    FoldersCreatedCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorSummary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_photo_organizer_runs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_photo_organizer_moves_run",
                table: "photo_organizer_moves",
                columns: new[] { "RunId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_photo_organizer_runs_job",
                table: "photo_organizer_runs",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "ix_photo_organizer_runs_owner_created",
                table: "photo_organizer_runs",
                columns: new[] { "OwnerUserId", "CreatedAt" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "photo_organizer_moves");

            migrationBuilder.DropTable(
                name: "photo_organizer_runs");
        }
    }
}
