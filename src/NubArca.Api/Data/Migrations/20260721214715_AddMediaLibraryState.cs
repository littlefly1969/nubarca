using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaLibraryState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MediaLibraryState",
                table: "file_items",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "MediaLibraryStateChangedAt",
                table: "file_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_file_items_owner_medialibrarystate_id",
                table: "file_items",
                columns: new[] { "OwnerUserId", "MediaLibraryState", "Id" },
                filter: "\"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_file_items_owner_medialibrarystate_id",
                table: "file_items");

            migrationBuilder.DropColumn(
                name: "MediaLibraryState",
                table: "file_items");

            migrationBuilder.DropColumn(
                name: "MediaLibraryStateChangedAt",
                table: "file_items");
        }
    }
}
