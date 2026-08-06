using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyUploadLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UploadEnabled",
                table: "party_album_links",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "UploadTokenHash",
                table: "party_album_links",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_party_album_links_upload_token_hash",
                table: "party_album_links",
                column: "UploadTokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_party_album_links_upload_token_hash",
                table: "party_album_links");

            migrationBuilder.DropColumn(
                name: "UploadEnabled",
                table: "party_album_links");

            migrationBuilder.DropColumn(
                name: "UploadTokenHash",
                table: "party_album_links");
        }
    }
}
