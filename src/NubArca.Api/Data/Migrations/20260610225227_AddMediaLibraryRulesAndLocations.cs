using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaLibraryRulesAndLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MediaPhotosExcluded",
                table: "folders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "MediaPhotosExcludedForChildren",
                table: "folders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "MediaVideosExcluded",
                table: "folders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "MediaVideosExcludedForChildren",
                table: "folders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "file_item_locations",
                columns: table => new
                {
                    FileItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: false),
                    Longitude = table.Column<double>(type: "double precision", nullable: false),
                    Altitude = table.Column<double>(type: "double precision", nullable: true),
                    TakenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SourceBlobMetadataId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_item_locations", x => x.FileItemId);
                    table.ForeignKey(
                        name: "FK_file_item_locations_file_items_FileItemId",
                        column: x => x.FileItemId,
                        principalTable: "file_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "media_library_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    AppliesToPhotos = table.Column<bool>(type: "boolean", nullable: false),
                    AppliesToVideos = table.Column<bool>(type: "boolean", nullable: false),
                    AppliesToChildren = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_library_rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_media_library_rules_folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_file_item_locations_owner_taken",
                table: "file_item_locations",
                columns: new[] { "OwnerUserId", "TakenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_media_library_rules_FolderId",
                table: "media_library_rules",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "ux_media_library_rules_owner_folder",
                table: "media_library_rules",
                columns: new[] { "OwnerUserId", "FolderId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "file_item_locations");

            migrationBuilder.DropTable(
                name: "media_library_rules");

            migrationBuilder.DropColumn(
                name: "MediaPhotosExcluded",
                table: "folders");

            migrationBuilder.DropColumn(
                name: "MediaPhotosExcludedForChildren",
                table: "folders");

            migrationBuilder.DropColumn(
                name: "MediaVideosExcluded",
                table: "folders");

            migrationBuilder.DropColumn(
                name: "MediaVideosExcludedForChildren",
                table: "folders");
        }
    }
}
