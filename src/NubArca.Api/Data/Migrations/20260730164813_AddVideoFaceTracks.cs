using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoFaceTracks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "video_face_analysis_statuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoSemanticIndexId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisVersion = table.Column<int>(type: "integer", nullable: false),
                    DetectionProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmbeddingProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PlannedFrameCount = table.Column<int>(type: "integer", nullable: false),
                    ProcessedFrameCount = table.Column<int>(type: "integer", nullable: false),
                    FailedFrameCount = table.Column<int>(type: "integer", nullable: false),
                    TrackCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_face_analysis_statuses", x => x.Id);
                    table.CheckConstraint("ck_video_face_analysis_statuses_counts_non_negative", "\"PlannedFrameCount\" >= 0 AND \"ProcessedFrameCount\" >= 0 AND \"FailedFrameCount\" >= 0 AND \"TrackCount\" >= 0 AND \"AttemptCount\" >= 0");
                    table.CheckConstraint("ck_video_face_analysis_statuses_version_positive", "\"AnalysisVersion\" > 0");
                    table.ForeignKey(
                        name: "FK_video_face_analysis_statuses_ai_profiles_DetectionProfileId",
                        column: x => x.DetectionProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_video_face_analysis_statuses_ai_profiles_EmbeddingProfileId",
                        column: x => x.EmbeddingProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_video_face_analysis_statuses_video_semantic_indexes_VideoSe~",
                        column: x => x.VideoSemanticIndexId,
                        principalTable: "video_semantic_indexes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "video_face_tracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoFaceAnalysisStatusId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackIndex = table.Column<int>(type: "integer", nullable: false),
                    StartMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    EndMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    RepresentativeTimestampMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    DetectionCount = table.Column<int>(type: "integer", nullable: false),
                    EmbeddingBytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    EmbeddingDimension = table.Column<int>(type: "integer", nullable: false),
                    QualityScore = table.Column<double>(type: "double precision", nullable: false),
                    RepresentativeBoundingBoxX = table.Column<double>(type: "double precision", nullable: false),
                    RepresentativeBoundingBoxY = table.Column<double>(type: "double precision", nullable: false),
                    RepresentativeBoundingBoxWidth = table.Column<double>(type: "double precision", nullable: false),
                    RepresentativeBoundingBoxHeight = table.Column<double>(type: "double precision", nullable: false),
                    RepresentativeCropBlobObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_face_tracks", x => x.Id);
                    table.CheckConstraint("ck_video_face_tracks_bbox_unit_range", "\"RepresentativeBoundingBoxX\" >= 0 AND \"RepresentativeBoundingBoxX\" <= 1 AND \"RepresentativeBoundingBoxY\" >= 0 AND \"RepresentativeBoundingBoxY\" <= 1 AND \"RepresentativeBoundingBoxWidth\" >= 0 AND \"RepresentativeBoundingBoxWidth\" <= 1 AND \"RepresentativeBoundingBoxHeight\" >= 0 AND \"RepresentativeBoundingBoxHeight\" <= 1");
                    table.CheckConstraint("ck_video_face_tracks_detections_positive", "\"DetectionCount\" > 0");
                    table.CheckConstraint("ck_video_face_tracks_dimension_positive", "\"EmbeddingDimension\" > 0");
                    table.CheckConstraint("ck_video_face_tracks_index_non_negative", "\"TrackIndex\" >= 0");
                    table.CheckConstraint("ck_video_face_tracks_interval_ordered", "\"StartMilliseconds\" >= 0 AND \"StartMilliseconds\" <= \"RepresentativeTimestampMilliseconds\" AND \"RepresentativeTimestampMilliseconds\" <= \"EndMilliseconds\"");
                    table.CheckConstraint("ck_video_face_tracks_quality_unit_range", "\"QualityScore\" >= 0 AND \"QualityScore\" <= 1");
                    table.ForeignKey(
                        name: "FK_video_face_tracks_blob_objects_RepresentativeCropBlobObject~",
                        column: x => x.RepresentativeCropBlobObjectId,
                        principalTable: "blob_objects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_video_face_tracks_video_face_analysis_statuses_VideoFaceAna~",
                        column: x => x.VideoFaceAnalysisStatusId,
                        principalTable: "video_face_analysis_statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_video_face_analysis_statuses_DetectionProfileId",
                table: "video_face_analysis_statuses",
                column: "DetectionProfileId");

            migrationBuilder.CreateIndex(
                name: "ix_video_face_analysis_statuses_profile_version_status",
                table: "video_face_analysis_statuses",
                columns: new[] { "EmbeddingProfileId", "AnalysisVersion", "Status" });

            migrationBuilder.CreateIndex(
                name: "ux_video_face_analysis_statuses_scope",
                table: "video_face_analysis_statuses",
                columns: new[] { "VideoSemanticIndexId", "AnalysisVersion", "DetectionProfileId", "EmbeddingProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_video_face_tracks_RepresentativeCropBlobObjectId",
                table: "video_face_tracks",
                column: "RepresentativeCropBlobObjectId");

            migrationBuilder.CreateIndex(
                name: "ux_video_face_tracks_analysis_ordinal",
                table: "video_face_tracks",
                columns: new[] { "VideoFaceAnalysisStatusId", "TrackIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "video_face_tracks");

            migrationBuilder.DropTable(
                name: "video_face_analysis_statuses");
        }
    }
}
