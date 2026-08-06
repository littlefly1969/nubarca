using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteUploadStaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StagingSessionId",
                table: "admin_import_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "remote_upload_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationFolderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StagingRelativeRoot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TotalFiles = table.Column<int>(type: "integer", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedFiles = table.Column<int>(type: "integer", nullable: false),
                    ReceivedBytes = table.Column<long>(type: "bigint", nullable: false),
                    VerifiedFiles = table.Column<int>(type: "integer", nullable: false),
                    FailedFiles = table.Column<int>(type: "integer", nullable: false),
                    AdminImportRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastErrorCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    LastErrorMessage = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_remote_upload_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "remote_upload_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    RelativePath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    LastModifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReceivedBytes = table.Column<long>(type: "bigint", nullable: false),
                    ChunkSizeBytes = table.Column<int>(type: "integer", nullable: false),
                    ExpectedChunkCount = table.Column<int>(type: "integer", nullable: false),
                    ReceivedChunkCount = table.Column<int>(type: "integer", nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_remote_upload_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_remote_upload_items_remote_upload_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "remote_upload_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "remote_upload_chunks",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChunkIndex = table.Column<int>(type: "integer", nullable: false),
                    SizeBytes = table.Column<int>(type: "integer", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_remote_upload_chunks", x => new { x.ItemId, x.ChunkIndex });
                    table.ForeignKey(
                        name: "FK_remote_upload_chunks_remote_upload_items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "remote_upload_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_remote_upload_items_session_status",
                table: "remote_upload_items",
                columns: new[] { "SessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ux_remote_upload_items_session_ordinal",
                table: "remote_upload_items",
                columns: new[] { "SessionId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_remote_upload_sessions_expires",
                table: "remote_upload_sessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "ix_remote_upload_sessions_owner_created",
                table: "remote_upload_sessions",
                columns: new[] { "CreatedByUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_remote_upload_sessions_status",
                table: "remote_upload_sessions",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "remote_upload_chunks");

            migrationBuilder.DropTable(
                name: "remote_upload_items");

            migrationBuilder.DropTable(
                name: "remote_upload_sessions");

            migrationBuilder.DropColumn(
                name: "StagingSessionId",
                table: "admin_import_runs");
        }
    }
}
