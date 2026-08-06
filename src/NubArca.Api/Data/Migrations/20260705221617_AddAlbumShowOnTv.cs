using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlbumShowOnTv : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowOnTv",
                table: "albums",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_albums_owner_show_on_tv",
                table: "albums",
                columns: new[] { "OwnerUserId", "ShowOnTv" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_albums_owner_show_on_tv",
                table: "albums");

            migrationBuilder.DropColumn(
                name: "ShowOnTv",
                table: "albums");
        }
    }
}
