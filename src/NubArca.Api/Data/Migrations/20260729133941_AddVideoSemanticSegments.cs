using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoSemanticSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "video_semantic_indexes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BlobObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SegmentationVersion = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsPermanentFailure = table.Column<bool>(type: "boolean", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    DurationMilliseconds = table.Column<long>(type: "bigint", nullable: true),
                    SegmentCount = table.Column<int>(type: "integer", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_semantic_indexes", x => x.Id);
                    table.CheckConstraint("ck_video_semantic_indexes_counts_non_negative", "\"SegmentCount\" >= 0 AND \"SampleCount\" >= 0 AND \"AttemptCount\" >= 0");
                    table.CheckConstraint("ck_video_semantic_indexes_duration_positive", "\"DurationMilliseconds\" IS NULL OR \"DurationMilliseconds\" > 0");
                    table.CheckConstraint("ck_video_semantic_indexes_version_positive", "\"SegmentationVersion\" > 0");
                    table.ForeignKey(
                        name: "FK_video_semantic_indexes_blob_objects_BlobObjectId",
                        column: x => x.BlobObjectId,
                        principalTable: "blob_objects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "video_semantic_segments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoSemanticIndexId = table.Column<Guid>(type: "uuid", nullable: false),
                    SegmentIndex = table.Column<int>(type: "integer", nullable: false),
                    StartMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    EndMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    BoundaryReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_semantic_segments", x => x.Id);
                    table.CheckConstraint("ck_video_semantic_segments_index_non_negative", "\"SegmentIndex\" >= 0");
                    table.CheckConstraint("ck_video_semantic_segments_interval", "\"StartMilliseconds\" >= 0 AND \"EndMilliseconds\" > \"StartMilliseconds\"");
                    table.ForeignKey(
                        name: "FK_video_semantic_segments_video_semantic_indexes_VideoSemanti~",
                        column: x => x.VideoSemanticIndexId,
                        principalTable: "video_semantic_indexes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "video_semantic_samples",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoSemanticSegmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SampleIndex = table.Column<int>(type: "integer", nullable: false),
                    TimestampMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    SelectionReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_semantic_samples", x => x.Id);
                    table.CheckConstraint("ck_video_semantic_samples_index_non_negative", "\"SampleIndex\" >= 0");
                    table.CheckConstraint("ck_video_semantic_samples_timestamp_non_negative", "\"TimestampMilliseconds\" >= 0");
                    table.ForeignKey(
                        name: "FK_video_semantic_samples_video_semantic_segments_VideoSemanti~",
                        column: x => x.VideoSemanticSegmentId,
                        principalTable: "video_semantic_segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_video_semantic_indexes_status_version",
                table: "video_semantic_indexes",
                columns: new[] { "Status", "SegmentationVersion" });

            migrationBuilder.CreateIndex(
                name: "ux_video_semantic_indexes_blob_version",
                table: "video_semantic_indexes",
                columns: new[] { "BlobObjectId", "SegmentationVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_video_semantic_samples_segment_ordinal",
                table: "video_semantic_samples",
                columns: new[] { "VideoSemanticSegmentId", "SampleIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_video_semantic_segments_index_start",
                table: "video_semantic_segments",
                columns: new[] { "VideoSemanticIndexId", "StartMilliseconds" });

            migrationBuilder.CreateIndex(
                name: "ux_video_semantic_segments_index_ordinal",
                table: "video_semantic_segments",
                columns: new[] { "VideoSemanticIndexId", "SegmentIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "video_semantic_samples");

            migrationBuilder.DropTable(
                name: "video_semantic_segments");

            migrationBuilder.DropTable(
                name: "video_semantic_indexes");
        }
    }
}
