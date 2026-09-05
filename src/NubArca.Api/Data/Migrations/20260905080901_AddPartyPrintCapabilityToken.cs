using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyPrintCapabilityToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PrintTokenHash",
                table: "party_album_links",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_party_album_links_print_token_hash",
                table: "party_album_links",
                column: "PrintTokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_party_album_links_print_token_hash",
                table: "party_album_links");

            migrationBuilder.DropColumn(
                name: "PrintTokenHash",
                table: "party_album_links");
        }
    }
}
