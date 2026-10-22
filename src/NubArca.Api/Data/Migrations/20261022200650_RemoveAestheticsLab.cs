using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAestheticsLab : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The Aesthetics Lab is retired with everything it stored. Each lab
            // item and each lab derivative owned exactly one blob reference, so
            // release them first with THE release rule (BlobService.
            // ReleaseReferenceAsync): decrement while positive, and start the
            // purge grace window on the transition to zero. A blob a FileItem
            // still references keeps that reference; bytes only the lab used
            // become purge-eligible and the BlobJanitor reclaims them.
            migrationBuilder.Sql(@"
                WITH lab_refs AS (
                    SELECT ""BlobObjectId"" AS id, count(*) AS n FROM (
                        SELECT ""BlobObjectId"" FROM aesthetic_lab_items
                        UNION ALL
                        SELECT ""BlobObjectId"" FROM aesthetic_lab_derivatives
                    ) r
                    GROUP BY ""BlobObjectId""
                )
                UPDATE blob_objects b
                SET ""ReferenceCount"" = GREATEST(b.""ReferenceCount"" - lab_refs.n, 0),
                    ""PurgeEligibleAt"" = CASE
                        WHEN b.""ReferenceCount"" > 0 AND b.""ReferenceCount"" - lab_refs.n <= 0
                            THEN now()
                        ELSE b.""PurgeEligibleAt"" END
                FROM lab_refs
                WHERE b.""Id"" = lab_refs.id;");

            // No handler runs the analysis job any more: a queued one would only
            // fail, and its payload names a run that no longer exists.
            migrationBuilder.Sql(@"
                DELETE FROM background_jobs
                WHERE ""Type"" = 'ai.aesthetics.human-aesexpert.analyze';");

            // The permission is gone from the catalogue. RoleService already
            // ignores unknown keys, so this is tidiness, not correctness.
            migrationBuilder.Sql(@"
                DELETE FROM role_permissions WHERE ""PermissionKey"" = 'laboratory.aesthetics';");

            migrationBuilder.DropTable(
                name: "aesthetic_lab_derivatives");

            migrationBuilder.DropTable(
                name: "aesthetic_metrics");

            migrationBuilder.DropTable(
                name: "aesthetic_text_results");

            migrationBuilder.DropTable(
                name: "aesthetic_upload_sessions");

            migrationBuilder.DropTable(
                name: "aesthetic_analysis_runs");

            migrationBuilder.DropTable(
                name: "aesthetic_lab_items");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "aesthetic_lab_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BlobObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    LogicalContainerKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    SourceFileItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_aesthetic_lab_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_aesthetic_lab_items_blob_objects_BlobObjectId",
                        column: x => x.BlobObjectId,
                        principalTable: "blob_objects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_aesthetic_lab_items_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "aesthetic_upload_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AcceptedCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MaxFiles = table.Column<int>(type: "integer", nullable: false),
                    MaxTotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RejectedCount = table.Column<int>(type: "integer", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UsedBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_aesthetic_upload_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_aesthetic_upload_sessions_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "aesthetic_analysis_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AestheticLabItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    BackgroundJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedCapabilities = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    ModelName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ModelRevision = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreprocessingProfileKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProfileKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RawOutputJson = table.Column<string>(type: "jsonb", nullable: true),
                    RequestedCapabilities = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RuntimeName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RuntimeVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WarningsJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_aesthetic_analysis_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_aesthetic_analysis_runs_aesthetic_lab_items_AestheticLabIte~",
                        column: x => x.AestheticLabItemId,
                        principalTable: "aesthetic_lab_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_aesthetic_analysis_runs_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "aesthetic_lab_derivatives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AestheticLabItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlobObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Size = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_aesthetic_lab_derivatives", x => x.Id);
                    table.ForeignKey(
                        name: "FK_aesthetic_lab_derivatives_aesthetic_lab_items_AestheticLabI~",
                        column: x => x.AestheticLabItemId,
                        principalTable: "aesthetic_lab_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_aesthetic_lab_derivatives_blob_objects_BlobObjectId",
                        column: x => x.BlobObjectId,
                        principalTable: "blob_objects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "aesthetic_metrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MetricGroup = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MetricKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MetricVersion = table.Column<int>(type: "integer", nullable: false),
                    NumericValue = table.Column<double>(type: "double precision", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScaleMax = table.Column<double>(type: "double precision", nullable: false),
                    ScaleMin = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_aesthetic_metrics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_aesthetic_metrics_aesthetic_analysis_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "aesthetic_analysis_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "aesthetic_text_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PromptTemplateVersion = table.Column<int>(type: "integer", nullable: true),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    TextKind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_aesthetic_text_results", x => x.Id);
                    table.ForeignKey(
                        name: "FK_aesthetic_text_results_aesthetic_analysis_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "aesthetic_analysis_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_aesthetic_runs_item_created",
                table: "aesthetic_analysis_runs",
                columns: new[] { "AestheticLabItemId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_aesthetic_runs_item_status",
                table: "aesthetic_analysis_runs",
                columns: new[] { "AestheticLabItemId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ix_aesthetic_runs_owner",
                table: "aesthetic_analysis_runs",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "ix_aesthetic_lab_derivatives_blob_object",
                table: "aesthetic_lab_derivatives",
                column: "BlobObjectId");

            migrationBuilder.CreateIndex(
                name: "ux_aesthetic_lab_derivatives_item_size",
                table: "aesthetic_lab_derivatives",
                columns: new[] { "AestheticLabItemId", "Size" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_aesthetic_lab_items_blob_object",
                table: "aesthetic_lab_items",
                column: "BlobObjectId");

            migrationBuilder.CreateIndex(
                name: "ix_aesthetic_lab_items_owner_created",
                table: "aesthetic_lab_items",
                columns: new[] { "OwnerUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_aesthetic_lab_items_owner_blob",
                table: "aesthetic_lab_items",
                columns: new[] { "OwnerUserId", "BlobObjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_aesthetic_metrics_key",
                table: "aesthetic_metrics",
                column: "MetricKey");

            migrationBuilder.CreateIndex(
                name: "ux_aesthetic_metrics_run_key",
                table: "aesthetic_metrics",
                columns: new[] { "RunId", "MetricKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_aesthetic_text_results_run_kind",
                table: "aesthetic_text_results",
                columns: new[] { "RunId", "TextKind" });

            migrationBuilder.CreateIndex(
                name: "ix_aesthetic_upload_sessions_expires",
                table: "aesthetic_upload_sessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "ix_aesthetic_upload_sessions_owner_created",
                table: "aesthetic_upload_sessions",
                columns: new[] { "OwnerUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_aesthetic_upload_sessions_token_hash",
                table: "aesthetic_upload_sessions",
                column: "TokenHash",
                unique: true);
        }
    }
}
