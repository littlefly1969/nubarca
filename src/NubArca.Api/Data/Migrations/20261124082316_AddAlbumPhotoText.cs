using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlbumPhotoText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestFingerprint",
                table: "party_print_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoText",
                table: "album_items",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoTextLayout",
                table: "album_items",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PhotoTextVersion",
                table: "album_items",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestFingerprint",
                table: "party_print_requests");

            migrationBuilder.DropColumn(
                name: "PhotoText",
                table: "album_items");

            migrationBuilder.DropColumn(
                name: "PhotoTextLayout",
                table: "album_items");

            migrationBuilder.DropColumn(
                name: "PhotoTextVersion",
                table: "album_items");
        }
    }
}
