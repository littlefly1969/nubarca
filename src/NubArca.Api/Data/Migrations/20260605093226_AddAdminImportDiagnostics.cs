using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminImportDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BlobDbMillis",
                table: "admin_import_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CancelRequested",
                table: "admin_import_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "FileItemMillis",
                table: "admin_import_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FolderMillis",
                table: "admin_import_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "HashMillis",
                table: "admin_import_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MetadataMillis",
                table: "admin_import_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReadMillis",
                table: "admin_import_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ThumbnailMillis",
                table: "admin_import_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WriteMillis",
                table: "admin_import_runs",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlobDbMillis",
                table: "admin_import_runs");

            migrationBuilder.DropColumn(
                name: "CancelRequested",
                table: "admin_import_runs");

            migrationBuilder.DropColumn(
                name: "FileItemMillis",
                table: "admin_import_runs");

            migrationBuilder.DropColumn(
                name: "FolderMillis",
                table: "admin_import_runs");

            migrationBuilder.DropColumn(
                name: "HashMillis",
                table: "admin_import_runs");

            migrationBuilder.DropColumn(
                name: "MetadataMillis",
                table: "admin_import_runs");

            migrationBuilder.DropColumn(
                name: "ReadMillis",
                table: "admin_import_runs");

            migrationBuilder.DropColumn(
                name: "ThumbnailMillis",
                table: "admin_import_runs");

            migrationBuilder.DropColumn(
                name: "WriteMillis",
                table: "admin_import_runs");
        }
    }
}
