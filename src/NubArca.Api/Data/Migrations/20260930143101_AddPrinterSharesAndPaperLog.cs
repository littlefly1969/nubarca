using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterSharesAndPaperLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LoadedPaperChangedAt",
                table: "printer_devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LoadedPaperChangedByUserId",
                table: "printer_devices",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "printer_shares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrinterDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GranteeUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    MaxSheets = table.Column<int>(type: "integer", nullable: true),
                    UsedSheets = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_printer_shares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_printer_shares_printer_devices_PrinterDeviceId",
                        column: x => x.PrinterDeviceId,
                        principalTable: "printer_devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_printer_shares_users_GranteeUserId",
                        column: x => x.GranteeUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_printer_shares_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_printer_shares_grantee",
                table: "printer_shares",
                column: "GranteeUserId");

            migrationBuilder.CreateIndex(
                name: "IX_printer_shares_OwnerUserId",
                table: "printer_shares",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "ux_printer_shares_active",
                table: "printer_shares",
                columns: new[] { "PrinterDeviceId", "GranteeUserId" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "printer_shares");

            migrationBuilder.DropColumn(
                name: "LoadedPaperChangedAt",
                table: "printer_devices");

            migrationBuilder.DropColumn(
                name: "LoadedPaperChangedByUserId",
                table: "printer_devices");
        }
    }
}
