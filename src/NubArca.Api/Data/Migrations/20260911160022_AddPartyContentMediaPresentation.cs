using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyContentMediaPresentation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MediaPresentation",
                table: "party_guest_contents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "inline");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_guest_contents_media_presentation",
                table: "party_guest_contents",
                sql: "\"MediaPresentation\" IN ('inline', 'poster')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_party_guest_contents_media_presentation",
                table: "party_guest_contents");

            migrationBuilder.DropColumn(
                name: "MediaPresentation",
                table: "party_guest_contents");
        }
    }
}
