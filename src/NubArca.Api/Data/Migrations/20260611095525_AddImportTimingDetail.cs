using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImportTimingDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DetectMillis",
                table: "admin_import_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ItemDbMillis",
                table: "admin_import_runs",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DetectMillis",
                table: "admin_import_runs");

            migrationBuilder.DropColumn(
                name: "ItemDbMillis",
                table: "admin_import_runs");
        }
    }
}
