using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeletedContentTombstonesAndImportSkip : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SkipExistingContent",
                table: "remote_upload_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SkipPreviouslyDeleted",
                table: "remote_upload_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SkipExistingContent",
                table: "admin_import_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SkipPreviouslyDeleted",
                table: "admin_import_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SkippedAlreadyPresentFiles",
                table: "admin_import_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SkippedPreviouslyDeletedFiles",
                table: "admin_import_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "owner_deleted_content_tombstones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FingerprintScheme = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FirstDeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastDeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedCount = table.Column<int>(type: "integer", nullable: false),
                    LastFileNameSnapshot = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    LastDeletedFromPathSnapshot = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_owner_deleted_content_tombstones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_owner_deleted_content_tombstones_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_owner_deleted_content_owner_scheme_fingerprint",
                table: "owner_deleted_content_tombstones",
                columns: new[] { "OwnerUserId", "FingerprintScheme", "ContentFingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "owner_deleted_content_tombstones");

            migrationBuilder.DropColumn(
                name: "SkipExistingContent",
                table: "remote_upload_sessions");

            migrationBuilder.DropColumn(
                name: "SkipPreviouslyDeleted",
                table: "remote_upload_sessions");

            migrationBuilder.DropColumn(
                name: "SkipExistingContent",
                table: "admin_import_runs");

            migrationBuilder.DropColumn(
                name: "SkipPreviouslyDeleted",
                table: "admin_import_runs");

            migrationBuilder.DropColumn(
                name: "SkippedAlreadyPresentFiles",
                table: "admin_import_runs");

            migrationBuilder.DropColumn(
                name: "SkippedPreviouslyDeletedFiles",
                table: "admin_import_runs");
        }
    }
}
