using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyPrintPerGuestBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PhotoPrintsPerGuest",
                table: "party_print_profiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "StripPrintsPerGuest",
                table: "party_print_profiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AcceptedPhotoPrintCount",
                table: "party_participants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AcceptedStripPrintCount",
                table: "party_participants",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PhotoPrintsPerGuest",
                table: "party_print_profiles");

            migrationBuilder.DropColumn(
                name: "StripPrintsPerGuest",
                table: "party_print_profiles");

            migrationBuilder.DropColumn(
                name: "AcceptedPhotoPrintCount",
                table: "party_participants");

            migrationBuilder.DropColumn(
                name: "AcceptedStripPrintCount",
                table: "party_participants");
        }
    }
}
