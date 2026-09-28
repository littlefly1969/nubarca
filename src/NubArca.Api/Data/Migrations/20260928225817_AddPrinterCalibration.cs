using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterCalibration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CalibrationBrightness",
                table: "printer_devices",
                type: "double precision",
                nullable: false,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<double>(
                name: "CalibrationContrast",
                table: "printer_devices",
                type: "double precision",
                nullable: false,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<double>(
                name: "CalibrationGamma",
                table: "printer_devices",
                type: "double precision",
                nullable: false,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<double>(
                name: "CalibrationSaturation",
                table: "printer_devices",
                type: "double precision",
                nullable: false,
                defaultValue: 1.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalibrationBrightness",
                table: "printer_devices");

            migrationBuilder.DropColumn(
                name: "CalibrationContrast",
                table: "printer_devices");

            migrationBuilder.DropColumn(
                name: "CalibrationGamma",
                table: "printer_devices");

            migrationBuilder.DropColumn(
                name: "CalibrationSaturation",
                table: "printer_devices");
        }
    }
}
