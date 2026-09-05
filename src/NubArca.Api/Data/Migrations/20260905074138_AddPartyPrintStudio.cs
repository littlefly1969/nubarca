using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyPrintStudio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "party_print_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyAlbumId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    PrintStationId = table.Column<Guid>(type: "uuid", nullable: true),
                    PrinterDeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    PhotoEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PhotoMaxPrints = table.Column<int>(type: "integer", nullable: false),
                    PhotoAcceptedCount = table.Column<int>(type: "integer", nullable: false),
                    StripEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    StripMaxPrints = table.Column<int>(type: "integer", nullable: false),
                    StripAcceptedCount = table.Column<int>(type: "integer", nullable: false),
                    FooterText = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    PublicSequenceNext = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_print_profiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_party_print_profiles_albums_PartyAlbumId",
                        column: x => x.PartyAlbumId,
                        principalTable: "albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_party_print_profiles_print_stations_PrintStationId",
                        column: x => x.PrintStationId,
                        principalTable: "print_stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_party_print_profiles_printer_devices_PrinterDeviceId",
                        column: x => x.PrinterDeviceId,
                        principalTable: "printer_devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_party_print_profiles_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_print_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyAlbumId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKeyHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Product = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PrintJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_print_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_party_print_requests_albums_PartyAlbumId",
                        column: x => x.PartyAlbumId,
                        principalTable: "albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_party_print_requests_print_jobs_PrintJobId",
                        column: x => x.PrintJobId,
                        principalTable: "print_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "print_job_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrintJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    SlotIndex = table.Column<int>(type: "integer", nullable: false),
                    FileItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    CropX = table.Column<double>(type: "double precision", nullable: false),
                    CropY = table.Column<double>(type: "double precision", nullable: false),
                    CropWidth = table.Column<double>(type: "double precision", nullable: false),
                    CropHeight = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_print_job_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_print_job_sources_file_items_FileItemId",
                        column: x => x.FileItemId,
                        principalTable: "file_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_print_job_sources_print_jobs_PrintJobId",
                        column: x => x.PrintJobId,
                        principalTable: "print_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_party_print_profiles_owner",
                table: "party_print_profiles",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_party_print_profiles_PrinterDeviceId",
                table: "party_print_profiles",
                column: "PrinterDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_party_print_profiles_PrintStationId",
                table: "party_print_profiles",
                column: "PrintStationId");

            migrationBuilder.CreateIndex(
                name: "ux_party_print_profiles_album",
                table: "party_print_profiles",
                column: "PartyAlbumId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_party_print_requests_PrintJobId",
                table: "party_print_requests",
                column: "PrintJobId");

            migrationBuilder.CreateIndex(
                name: "ux_party_print_requests_album_key",
                table: "party_print_requests",
                columns: new[] { "PartyAlbumId", "IdempotencyKeyHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_print_job_sources_FileItemId",
                table: "print_job_sources",
                column: "FileItemId");

            migrationBuilder.CreateIndex(
                name: "ux_print_job_sources_job_slot",
                table: "print_job_sources",
                columns: new[] { "PrintJobId", "SlotIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_print_profiles");

            migrationBuilder.DropTable(
                name: "party_print_requests");

            migrationBuilder.DropTable(
                name: "print_job_sources");
        }
    }
}
