using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlateFaceRedaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plate_face_redaction_boxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlateImageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    BoundingBoxX = table.Column<double>(type: "double precision", nullable: false),
                    BoundingBoxY = table.Column<double>(type: "double precision", nullable: false),
                    BoundingBoxWidth = table.Column<double>(type: "double precision", nullable: false),
                    BoundingBoxHeight = table.Column<double>(type: "double precision", nullable: false),
                    ModelProfileKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plate_face_redaction_boxes", x => x.Id);
                    table.CheckConstraint("ck_plate_face_redaction_boxes_height_non_negative", "\"BoundingBoxHeight\" >= 0");
                    table.CheckConstraint("ck_plate_face_redaction_boxes_width_non_negative", "\"BoundingBoxWidth\" >= 0");
                    table.ForeignKey(
                        name: "FK_plate_face_redaction_boxes_plate_images_PlateImageId",
                        column: x => x.PlateImageId,
                        principalTable: "plate_images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_plate_face_redaction_boxes_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plate_redacted_media",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlateImageId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BlurFaces = table.Column<bool>(type: "boolean", nullable: false),
                    ProfileKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RedactionMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PixelBlockSize = table.Column<int>(type: "integer", nullable: false),
                    BlobObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plate_redacted_media", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plate_redacted_media_blob_objects_BlobObjectId",
                        column: x => x.BlobObjectId,
                        principalTable: "blob_objects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_plate_redacted_media_plate_images_PlateImageId",
                        column: x => x.PlateImageId,
                        principalTable: "plate_images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_plate_redacted_media_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_plate_face_redaction_boxes_image",
                table: "plate_face_redaction_boxes",
                column: "PlateImageId");

            migrationBuilder.CreateIndex(
                name: "ix_plate_face_redaction_boxes_owner_image",
                table: "plate_face_redaction_boxes",
                columns: new[] { "OwnerUserId", "PlateImageId" });

            migrationBuilder.CreateIndex(
                name: "ix_plate_redacted_media_blob_object",
                table: "plate_redacted_media",
                column: "BlobObjectId");

            migrationBuilder.CreateIndex(
                name: "ix_plate_redacted_media_image",
                table: "plate_redacted_media",
                column: "PlateImageId");

            migrationBuilder.CreateIndex(
                name: "ix_plate_redacted_media_lookup",
                table: "plate_redacted_media",
                columns: new[] { "OwnerUserId", "PlateImageId", "SourceKind", "ProfileKey", "RedactionMode" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plate_face_redaction_boxes");

            migrationBuilder.DropTable(
                name: "plate_redacted_media");
        }
    }
}
