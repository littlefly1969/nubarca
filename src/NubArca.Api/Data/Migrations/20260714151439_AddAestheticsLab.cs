using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAestheticsLab : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "aesthetic_lab_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlobObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceFileItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginalFileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    LogicalContainerKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "aesthetic_analysis_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AestheticLabItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ModelName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ModelRevision = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RuntimeName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RuntimeVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PreprocessingProfileKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestedCapabilities = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CompletedCapabilities = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BackgroundJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    RawOutputJson = table.Column<string>(type: "jsonb", nullable: true),
                    WarningsJson = table.Column<string>(type: "jsonb", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true)
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
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AestheticLabItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Size = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BlobObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    MetricKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MetricGroup = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NumericValue = table.Column<double>(type: "double precision", nullable: false),
                    ScaleMin = table.Column<double>(type: "double precision", nullable: false),
                    ScaleMax = table.Column<double>(type: "double precision", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    MetricVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    TextKind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    PromptTemplateVersion = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "aesthetic_lab_derivatives");

            migrationBuilder.DropTable(
                name: "aesthetic_metrics");

            migrationBuilder.DropTable(
                name: "aesthetic_text_results");

            migrationBuilder.DropTable(
                name: "aesthetic_analysis_runs");

            migrationBuilder.DropTable(
                name: "aesthetic_lab_items");
        }
    }
}
