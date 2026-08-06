using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyFaceSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "party_face_search_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlbumId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyAlbumLinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResultCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_face_search_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_party_face_search_sessions_albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_party_face_search_sessions_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_face_search_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyFaceSearchSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_face_search_results", x => x.Id);
                    table.ForeignKey(
                        name: "FK_party_face_search_results_file_items_FileItemId",
                        column: x => x.FileItemId,
                        principalTable: "file_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_party_face_search_results_party_face_search_sessions_PartyF~",
                        column: x => x.PartyFaceSearchSessionId,
                        principalTable: "party_face_search_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_party_face_search_results_FileItemId",
                table: "party_face_search_results",
                column: "FileItemId");

            migrationBuilder.CreateIndex(
                name: "ix_party_face_search_results_session_rank",
                table: "party_face_search_results",
                columns: new[] { "PartyFaceSearchSessionId", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_party_face_search_sessions_AlbumId",
                table: "party_face_search_sessions",
                column: "AlbumId");

            migrationBuilder.CreateIndex(
                name: "ix_party_face_search_sessions_owner_album_expires",
                table: "party_face_search_sessions",
                columns: new[] { "OwnerUserId", "AlbumId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_face_search_results");

            migrationBuilder.DropTable(
                name: "party_face_search_sessions");
        }
    }
}
