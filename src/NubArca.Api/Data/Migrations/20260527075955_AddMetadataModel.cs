using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMetadataModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blob_metadata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BlobObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    DetectedContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    MediaCategory = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DetectedFormat = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    PixelCount = table.Column<long>(type: "bigint", nullable: true),
                    ThumbnailStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExtractionStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExtractionErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExtractedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RawMetadataJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blob_metadata", x => x.Id);
                    table.CheckConstraint("ck_blob_metadata_height_positive", "\"Height\" IS NULL OR \"Height\" > 0");
                    table.CheckConstraint("ck_blob_metadata_pixel_count_non_negative", "\"PixelCount\" IS NULL OR \"PixelCount\" >= 0");
                    table.CheckConstraint("ck_blob_metadata_size_bytes_non_negative", "\"SizeBytes\" >= 0");
                    table.CheckConstraint("ck_blob_metadata_width_positive", "\"Width\" IS NULL OR \"Width\" > 0");
                    table.ForeignKey(
                        name: "FK_blob_metadata_blob_objects_BlobObjectId",
                        column: x => x.BlobObjectId,
                        principalTable: "blob_objects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "file_item_user_metadata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    TagsJson = table.Column<string>(type: "text", nullable: true),
                    Rating = table.Column<int>(type: "integer", nullable: true),
                    IsFavorite = table.Column<bool>(type: "boolean", nullable: false),
                    DateTakenOverride = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LocationOverride = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_item_user_metadata", x => x.Id);
                    table.CheckConstraint("ck_file_item_user_metadata_rating_range", "\"Rating\" IS NULL OR (\"Rating\" >= 0 AND \"Rating\" <= 5)");
                    table.ForeignKey(
                        name: "FK_file_item_user_metadata_file_items_FileItemId",
                        column: x => x.FileItemId,
                        principalTable: "file_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_blob_metadata_blob_object",
                table: "blob_metadata",
                column: "BlobObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_file_item_user_metadata_file",
                table: "file_item_user_metadata",
                column: "FileItemId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blob_metadata");

            migrationBuilder.DropTable(
                name: "file_item_user_metadata");
        }
    }
}
