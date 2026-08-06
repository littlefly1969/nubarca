using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobSchedulingV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CheckpointJson",
                table: "background_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SliceNumber",
                table: "background_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "YieldReason",
                table: "background_jobs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_background_jobs_status_priority_available",
                table: "background_jobs",
                columns: new[] { "Status", "Priority", "AvailableAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_background_jobs_status_priority_available",
                table: "background_jobs");

            migrationBuilder.DropColumn(
                name: "CheckpointJson",
                table: "background_jobs");

            migrationBuilder.DropColumn(
                name: "SliceNumber",
                table: "background_jobs");

            migrationBuilder.DropColumn(
                name: "YieldReason",
                table: "background_jobs");
        }
    }
}
