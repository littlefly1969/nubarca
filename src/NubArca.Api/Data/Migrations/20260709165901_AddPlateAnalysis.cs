using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlateAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plate_analysis_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlateImageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorMessageSafe = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ProfileKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plate_analysis_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plate_analysis_jobs_plate_images_PlateImageId",
                        column: x => x.PlateImageId,
                        principalTable: "plate_images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_plate_analysis_jobs_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plate_analysis_model_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlateAnalysisJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DetectorName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DetectorVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    OcrName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OcrVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    InputWidth = table.Column<int>(type: "integer", nullable: false),
                    InputHeight = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    DetectionsCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plate_analysis_model_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plate_analysis_model_runs_plate_analysis_jobs_PlateAnalysis~",
                        column: x => x.PlateAnalysisJobId,
                        principalTable: "plate_analysis_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plate_detections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlateImageId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlateAnalysisJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NormalizedText = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CountryHint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    RegionHint = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PlateConfidence = table.Column<double>(type: "double precision", nullable: false),
                    OcrConfidence = table.Column<double>(type: "double precision", nullable: false),
                    CombinedConfidence = table.Column<double>(type: "double precision", nullable: false),
                    BoundingBoxX = table.Column<double>(type: "double precision", nullable: false),
                    BoundingBoxY = table.Column<double>(type: "double precision", nullable: false),
                    BoundingBoxWidth = table.Column<double>(type: "double precision", nullable: false),
                    BoundingBoxHeight = table.Column<double>(type: "double precision", nullable: false),
                    PolygonJson = table.Column<string>(type: "text", nullable: true),
                    ModelProfileKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plate_detections", x => x.Id);
                    table.CheckConstraint("ck_plate_detections_box_height_non_negative", "\"BoundingBoxHeight\" >= 0");
                    table.CheckConstraint("ck_plate_detections_box_width_non_negative", "\"BoundingBoxWidth\" >= 0");
                    table.ForeignKey(
                        name: "FK_plate_detections_plate_analysis_jobs_PlateAnalysisJobId",
                        column: x => x.PlateAnalysisJobId,
                        principalTable: "plate_analysis_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_plate_detections_plate_images_PlateImageId",
                        column: x => x.PlateImageId,
                        principalTable: "plate_images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_plate_detections_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_plate_analysis_jobs_owner_image_status",
                table: "plate_analysis_jobs",
                columns: new[] { "OwnerUserId", "PlateImageId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ix_plate_analysis_jobs_owner_requested",
                table: "plate_analysis_jobs",
                columns: new[] { "OwnerUserId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_plate_analysis_jobs_PlateImageId",
                table: "plate_analysis_jobs",
                column: "PlateImageId");

            migrationBuilder.CreateIndex(
                name: "ix_plate_analysis_model_runs_job",
                table: "plate_analysis_model_runs",
                column: "PlateAnalysisJobId");

            migrationBuilder.CreateIndex(
                name: "ix_plate_detections_job",
                table: "plate_detections",
                column: "PlateAnalysisJobId");

            migrationBuilder.CreateIndex(
                name: "ix_plate_detections_owner_image",
                table: "plate_detections",
                columns: new[] { "OwnerUserId", "PlateImageId" });

            migrationBuilder.CreateIndex(
                name: "ix_plate_detections_owner_normalized_text",
                table: "plate_detections",
                columns: new[] { "OwnerUserId", "NormalizedText" });

            migrationBuilder.CreateIndex(
                name: "IX_plate_detections_PlateImageId",
                table: "plate_detections",
                column: "PlateImageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plate_analysis_model_runs");

            migrationBuilder.DropTable(
                name: "plate_detections");

            migrationBuilder.DropTable(
                name: "plate_analysis_jobs");
        }
    }
}
