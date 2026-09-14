using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyContentMediaFrame : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "MediaCropCenterX",
                table: "party_guest_contents",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MediaCropCenterY",
                table: "party_guest_contents",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MediaCropZoom",
                table: "party_guest_contents",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaOrientation",
                table: "party_guest_contents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_guest_contents_media_crop",
                table: "party_guest_contents",
                sql: "\"MediaCropZoom\" BETWEEN 1 AND 4 AND \"MediaCropCenterX\" BETWEEN 0 AND 1 AND \"MediaCropCenterY\" BETWEEN 0 AND 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_guest_contents_media_orientation",
                table: "party_guest_contents",
                sql: "\"MediaOrientation\" IN ('portrait', 'landscape')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_party_guest_contents_media_crop",
                table: "party_guest_contents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_party_guest_contents_media_orientation",
                table: "party_guest_contents");

            migrationBuilder.DropColumn(
                name: "MediaCropCenterX",
                table: "party_guest_contents");

            migrationBuilder.DropColumn(
                name: "MediaCropCenterY",
                table: "party_guest_contents");

            migrationBuilder.DropColumn(
                name: "MediaCropZoom",
                table: "party_guest_contents");

            migrationBuilder.DropColumn(
                name: "MediaOrientation",
                table: "party_guest_contents");
        }
    }
}
