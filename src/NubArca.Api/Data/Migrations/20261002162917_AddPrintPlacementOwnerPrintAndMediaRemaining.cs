using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintPlacementOwnerPrintAndMediaRemaining : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_party_guestbook_entries_crop",
                table: "party_guestbook_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_party_guest_contents_media_crop",
                table: "party_guest_contents");

            migrationBuilder.AddColumn<DateTime>(
                name: "MediaRemainingObservedAt",
                table: "printer_devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MediaRemainingPrints",
                table: "printer_devices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PlacementCenterX",
                table: "print_job_sources",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PlacementCenterY",
                table: "print_job_sources",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PlacementZoom",
                table: "print_job_sources",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "owner_photo_print_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKeyHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    PrintJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_owner_photo_print_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_owner_photo_print_requests_print_jobs_PrintJobId",
                        column: x => x.PrintJobId,
                        principalTable: "print_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_owner_photo_print_requests_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_printer_devices_media_remaining",
                table: "printer_devices",
                sql: "\"MediaRemainingPrints\" IS NULL OR \"MediaRemainingPrints\" BETWEEN 0 AND 100000");

            migrationBuilder.AddCheckConstraint(
                name: "ck_print_job_sources_placement",
                table: "print_job_sources",
                sql: "(\"PlacementCenterX\" IS NULL AND \"PlacementCenterY\" IS NULL AND \"PlacementZoom\" IS NULL) OR (\"PlacementCenterX\" IS NOT NULL AND \"PlacementCenterY\" IS NOT NULL AND \"PlacementZoom\" IS NOT NULL AND \"PlacementCenterX\" BETWEEN 0 AND 1 AND \"PlacementCenterY\" BETWEEN 0 AND 1 AND \"PlacementZoom\" > 0 AND \"PlacementZoom\" <= 4)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_guestbook_entries_crop",
                table: "party_guestbook_entries",
                sql: "\"CropZoom\" > 0 AND \"CropZoom\" <= 4 AND \"CropCenterX\" BETWEEN 0 AND 1 AND \"CropCenterY\" BETWEEN 0 AND 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_guest_contents_media_crop",
                table: "party_guest_contents",
                sql: "\"MediaCropZoom\" > 0 AND \"MediaCropZoom\" <= 4 AND \"MediaCropCenterX\" BETWEEN 0 AND 1 AND \"MediaCropCenterY\" BETWEEN 0 AND 1");

            migrationBuilder.CreateIndex(
                name: "IX_owner_photo_print_requests_PrintJobId",
                table: "owner_photo_print_requests",
                column: "PrintJobId");

            migrationBuilder.CreateIndex(
                name: "ux_owner_photo_print_requests_owner_key",
                table: "owner_photo_print_requests",
                columns: new[] { "OwnerUserId", "IdempotencyKeyHash" },
                unique: true);
        }

        /// <inheritdoc />
        /// <summary>
        /// Runs to completion whatever the new application stored, but is NOT
        /// lossless — this is its non-reversibility contract:
        ///   * a guest book memory or party section photo framed below zoom 1
        ///     (zoomed out) is set to zoom 1, its centre kept: the covering
        ///     framing the previous application draws for it anyway. The
        ///     zoomed-out framing itself is not recoverable;
        ///   * the per-slot placements of party prints are dropped; each slot
        ///     keeps the crop that was stored beside it, and a sheet already
        ///     rendered is unaffected;
        ///   * the owner print idempotency records are dropped (their jobs stay
        ///     in print_jobs), and so is the printers' reported media count,
        ///     which is telemetry.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "owner_photo_print_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_printer_devices_media_remaining",
                table: "printer_devices");

            migrationBuilder.DropCheckConstraint(
                name: "ck_print_job_sources_placement",
                table: "print_job_sources");

            migrationBuilder.DropCheckConstraint(
                name: "ck_party_guestbook_entries_crop",
                table: "party_guestbook_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_party_guest_contents_media_crop",
                table: "party_guest_contents");

            migrationBuilder.DropColumn(
                name: "MediaRemainingObservedAt",
                table: "printer_devices");

            migrationBuilder.DropColumn(
                name: "MediaRemainingPrints",
                table: "printer_devices");

            migrationBuilder.DropColumn(
                name: "PlacementCenterX",
                table: "print_job_sources");

            migrationBuilder.DropColumn(
                name: "PlacementCenterY",
                table: "print_job_sources");

            migrationBuilder.DropColumn(
                name: "PlacementZoom",
                table: "print_job_sources");

            // Zoomed-out framings back to the cover the previous schema allows,
            // or the stricter checks below would refuse to be re-added.
            migrationBuilder.Sql(
                "UPDATE party_guestbook_entries SET \"CropZoom\" = 1 WHERE \"CropZoom\" < 1;");
            migrationBuilder.Sql(
                "UPDATE party_guest_contents SET \"MediaCropZoom\" = 1 WHERE \"MediaCropZoom\" < 1;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_guestbook_entries_crop",
                table: "party_guestbook_entries",
                sql: "\"CropZoom\" BETWEEN 1 AND 4 AND \"CropCenterX\" BETWEEN 0 AND 1 AND \"CropCenterY\" BETWEEN 0 AND 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_guest_contents_media_crop",
                table: "party_guest_contents",
                sql: "\"MediaCropZoom\" BETWEEN 1 AND 4 AND \"MediaCropCenterX\" BETWEEN 0 AND 1 AND \"MediaCropCenterY\" BETWEEN 0 AND 1");
        }
    }
}
