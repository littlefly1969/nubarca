using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyContentTextPlacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TextPlacement",
                table: "party_guest_contents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_guest_contents_text_placement",
                table: "party_guest_contents",
                sql: "\"TextPlacement\" IN ('overlay')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_party_guest_contents_text_placement",
                table: "party_guest_contents");

            migrationBuilder.DropColumn(
                name: "TextPlacement",
                table: "party_guest_contents");
        }
    }
}
