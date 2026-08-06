using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkAdminImportToJobsV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancelRequested",
                table: "admin_import_runs");

            migrationBuilder.CreateIndex(
                name: "ix_admin_import_runs_job",
                table: "admin_import_runs",
                column: "JobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_admin_import_runs_job",
                table: "admin_import_runs");

            migrationBuilder.AddColumn<bool>(
                name: "CancelRequested",
                table: "admin_import_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
