using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLithophaneQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lithophane_queue_items",
                columns: table => new
                {
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lithophane_queue_items", x => new { x.OwnerUserId, x.FileItemId });
                    table.ForeignKey(
                        name: "FK_lithophane_queue_items_file_items_FileItemId",
                        column: x => x.FileItemId,
                        principalTable: "file_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_lithophane_queue_items_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lithophane_queue_items_FileItemId",
                table: "lithophane_queue_items",
                column: "FileItemId");

            migrationBuilder.CreateIndex(
                name: "IX_lithophane_queue_items_OwnerUserId_AddedAt",
                table: "lithophane_queue_items",
                columns: new[] { "OwnerUserId", "AddedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lithophane_queue_items");
        }
    }
}
