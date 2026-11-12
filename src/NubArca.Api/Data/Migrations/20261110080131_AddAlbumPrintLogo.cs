using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlbumPrintLogo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PrintLogoFileItemId",
                table: "albums",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PrintLogoOptional",
                table: "albums",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrintLogoFileItemId",
                table: "albums");

            migrationBuilder.DropColumn(
                name: "PrintLogoOptional",
                table: "albums");
        }
    }
}
