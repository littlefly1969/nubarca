using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlbumScopedSelections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AlbumId",
                table: "media_lists",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_lists_AlbumId",
                table: "media_lists",
                column: "AlbumId");

            migrationBuilder.CreateIndex(
                name: "ux_media_lists_owner_album_selection",
                table: "media_lists",
                columns: new[] { "OwnerUserId", "AlbumId" },
                unique: true,
                filter: "\"Kind\" = 2");

            migrationBuilder.AddForeignKey(
                name: "FK_media_lists_albums_AlbumId",
                table: "media_lists",
                column: "AlbumId",
                principalTable: "albums",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Album-scoped selections mean nothing without their album column.
            migrationBuilder.Sql("DELETE FROM media_lists WHERE \"Kind\" = 2;");

            migrationBuilder.DropForeignKey(
                name: "FK_media_lists_albums_AlbumId",
                table: "media_lists");

            migrationBuilder.DropIndex(
                name: "IX_media_lists_AlbumId",
                table: "media_lists");

            migrationBuilder.DropIndex(
                name: "ux_media_lists_owner_album_selection",
                table: "media_lists");

            migrationBuilder.DropColumn(
                name: "AlbumId",
                table: "media_lists");
        }
    }
}
