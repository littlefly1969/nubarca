using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyUploadModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireUploadApproval",
                table: "party_album_links",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "party_upload_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlbumId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyAlbumLinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    FileItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ModeratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ModeratedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_upload_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_party_upload_items_albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_upload_items_file_items_FileItemId",
                        column: x => x.FileItemId,
                        principalTable: "file_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_party_upload_items_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_party_upload_items_AlbumId",
                table: "party_upload_items",
                column: "AlbumId");

            migrationBuilder.CreateIndex(
                name: "ix_party_upload_items_owner_album_status",
                table: "party_upload_items",
                columns: new[] { "OwnerUserId", "AlbumId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ux_party_upload_items_file_item",
                table: "party_upload_items",
                column: "FileItemId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_upload_items");

            migrationBuilder.DropColumn(
                name: "RequireUploadApproval",
                table: "party_album_links");
        }
    }
}
