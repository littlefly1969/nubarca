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

            migrationBuilder.AddColumn<string>(
                name: "SimulatedPurgeJobIds",
                table: "printer_devices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SimulatedPurgeSequence",
                table: "printer_devices",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

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
                name: "SimulatedPurgeJobIds",
                table: "printer_devices");

            migrationBuilder.DropColumn(
                name: "SimulatedPurgeSequence",
                table: "printer_devices");

            migrationBuilder.DropColumn(
                name: "SimulationAvailable",
                table: "printer_devices");

            migrationBuilder.DropColumn(
                name: "SimulationOnly",
                table: "printer_devices");

            migrationBuilder.DropColumn(
                name: "Simulated",
                table: "print_jobs");
        }
    }
}
