using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CleanUpBackgroundJobsModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LockedAt",
                table: "background_jobs");

            migrationBuilder.CreateIndex(
                name: "ix_background_jobs_status_created",
                table: "background_jobs",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_background_jobs_status_created",
                table: "background_jobs");

            migrationBuilder.AddColumn<DateTime>(
                name: "LockedAt",
                table: "background_jobs",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
