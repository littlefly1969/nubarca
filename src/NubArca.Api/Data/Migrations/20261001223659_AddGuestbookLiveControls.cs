using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestbookLiveControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GuestbookControlVersion",
                table: "party_album_links",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "GuestbookTvActive",
                table: "party_album_links",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "GuestbookViewingEnabled",
                table: "party_album_links",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GuestbookControlVersion",
                table: "party_album_links");

            migrationBuilder.DropColumn(
                name: "GuestbookTvActive",
                table: "party_album_links");

            migrationBuilder.DropColumn(
                name: "GuestbookViewingEnabled",
                table: "party_album_links");
        }
    }
}
