using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterSimulation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Simulated",
                table: "printer_devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SimulationAvailable",
                table: "printer_devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SimulationOnly",
                table: "printer_devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "SimulatedOutputPurgeRequestedAt",
                table: "print_stations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Simulated",
                table: "print_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Simulated",
                table: "printer_devices");

            migrationBuilder.DropColumn(
                name: "SimulationAvailable",
                table: "printer_devices");

            migrationBuilder.DropColumn(
                name: "SimulationOnly",
                table: "printer_devices");

            migrationBuilder.DropColumn(
                name: "SimulatedOutputPurgeRequestedAt",
                table: "print_stations");

            migrationBuilder.DropColumn(
                name: "Simulated",
                table: "print_jobs");
        }
    }
}
