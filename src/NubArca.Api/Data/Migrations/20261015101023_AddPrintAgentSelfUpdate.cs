using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintAgentSelfUpdate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AgentSelfUpdate",
                table: "print_stations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "AgentUpdateRequestedAt",
                table: "print_stations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentUpdateResult",
                table: "print_stations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AgentUpdateResultAt",
                table: "print_stations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentUpdateResultVersion",
                table: "print_stations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentUpdateTarget",
                table: "print_stations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentSelfUpdate",
                table: "print_stations");

            migrationBuilder.DropColumn(
                name: "AgentUpdateRequestedAt",
                table: "print_stations");

            migrationBuilder.DropColumn(
                name: "AgentUpdateResult",
                table: "print_stations");

            migrationBuilder.DropColumn(
                name: "AgentUpdateResultAt",
                table: "print_stations");

            migrationBuilder.DropColumn(
                name: "AgentUpdateResultVersion",
                table: "print_stations");

            migrationBuilder.DropColumn(
                name: "AgentUpdateTarget",
                table: "print_stations");
        }
    }
}
