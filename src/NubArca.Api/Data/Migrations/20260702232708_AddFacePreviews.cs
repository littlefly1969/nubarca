using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFacePreviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "face_previews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FaceDetectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlobObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Size = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_face_previews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_face_previews_face_detections_FaceDetectionId",
                        column: x => x.FaceDetectionId,
                        principalTable: "face_detections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_face_previews_face_size",
                table: "face_previews",
                columns: new[] { "FaceDetectionId", "Size" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "face_previews");
        }
    }
}
