using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobLeaseHeartbeatProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CancellationRequested",
                table: "background_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "HeartbeatAt",
                table: "background_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseUntil",
                table: "background_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressCurrent",
                table: "background_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProgressMessage",
                table: "background_jobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressTotal",
                table: "background_jobs",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancellationRequested",
                table: "background_jobs");

            migrationBuilder.DropColumn(
                name: "HeartbeatAt",
                table: "background_jobs");

            migrationBuilder.DropColumn(
                name: "LeaseUntil",
                table: "background_jobs");

            migrationBuilder.DropColumn(
                name: "ProgressCurrent",
                table: "background_jobs");

            migrationBuilder.DropColumn(
                name: "ProgressMessage",
                table: "background_jobs");

            migrationBuilder.DropColumn(
                name: "ProgressTotal",
                table: "background_jobs");
        }
    }
}
