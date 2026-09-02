using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "print_stations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    DesiredState = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CredentialHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AgentVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_print_stations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_print_stations_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "print_station_enrollments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrintStationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_print_station_enrollments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_print_station_enrollments_print_stations_PrintStationId",
                        column: x => x.PrintStationId,
                        principalTable: "print_stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "printer_devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrintStationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Manufacturer = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Model = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    AdapterKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    LastObservedState = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_printer_devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_printer_devices_print_stations_PrintStationId",
                        column: x => x.PrintStationId,
                        principalTable: "print_stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "print_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrintStationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrinterDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PublicSequence = table.Column<long>(type: "bigint", nullable: true),
                    Format = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    RenderSpecificationJson = table.Column<string>(type: "jsonb", nullable: false),
                    ArtifactStorageKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ArtifactContentType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ArtifactByteLength = table.Column<long>(type: "bigint", nullable: true),
                    ClaimTokenHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RenderedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_print_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_print_jobs_file_items_FileItemId",
                        column: x => x.FileItemId,
                        principalTable: "file_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_print_jobs_print_stations_PrintStationId",
                        column: x => x.PrintStationId,
                        principalTable: "print_stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_print_jobs_printer_devices_PrinterDeviceId",
                        column: x => x.PrinterDeviceId,
                        principalTable: "printer_devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_print_jobs_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_print_jobs_FileItemId",
                table: "print_jobs",
                column: "FileItemId");

            migrationBuilder.CreateIndex(
                name: "ix_print_jobs_owner_created",
                table: "print_jobs",
                columns: new[] { "OwnerUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_print_jobs_PrinterDeviceId",
                table: "print_jobs",
                column: "PrinterDeviceId");

            migrationBuilder.CreateIndex(
                name: "ix_print_jobs_station_state_created",
                table: "print_jobs",
                columns: new[] { "PrintStationId", "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_print_station_enrollments_station_expires",
                table: "print_station_enrollments",
                columns: new[] { "PrintStationId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "ux_print_station_enrollments_token_hash",
                table: "print_station_enrollments",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_print_stations_owner_created",
                table: "print_stations",
                columns: new[] { "OwnerUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_print_stations_credential_hash",
                table: "print_stations",
                column: "CredentialHash",
                unique: true,
                filter: "\"CredentialHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_printer_devices_station_device_key",
                table: "printer_devices",
                columns: new[] { "PrintStationId", "DeviceKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "print_jobs");

            migrationBuilder.DropTable(
                name: "print_station_enrollments");

            migrationBuilder.DropTable(
                name: "printer_devices");

            migrationBuilder.DropTable(
                name: "print_stations");
        }
    }
}
