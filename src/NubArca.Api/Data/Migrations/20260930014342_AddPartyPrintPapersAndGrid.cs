using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyPrintPapersAndGrid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LoadedPaperSize",
                table: "printer_devices",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "10x15");

            migrationBuilder.AddColumn<int>(
                name: "GridAcceptedCount",
                table: "party_print_profiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "GridEnabled",
                table: "party_print_profiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "GridMaxPrints",
                table: "party_print_profiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "GridPrintsPerGuest",
                table: "party_print_profiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AcceptedGridPrintCount",
                table: "party_participants",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LoadedPaperSize",
                table: "printer_devices");

            migrationBuilder.DropColumn(
                name: "GridAcceptedCount",
                table: "party_print_profiles");

            migrationBuilder.DropColumn(
                name: "GridEnabled",
                table: "party_print_profiles");

            migrationBuilder.DropColumn(
                name: "GridMaxPrints",
                table: "party_print_profiles");

            migrationBuilder.DropColumn(
                name: "GridPrintsPerGuest",
                table: "party_print_profiles");

            migrationBuilder.DropColumn(
                name: "AcceptedGridPrintCount",
                table: "party_participants");
        }
    }
}
