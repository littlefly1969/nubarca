using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyDisplayGrant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "party_display_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TvSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyAlbumLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_display_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_party_display_grants_party_album_links_PartyAlbumLinkId",
                        column: x => x.PartyAlbumLinkId,
                        principalTable: "party_album_links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_display_grants_tv_sessions_TvSessionId",
                        column: x => x.TvSessionId,
                        principalTable: "tv_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_party_display_grants_expires_at",
                table: "party_display_grants",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "ix_party_display_grants_link",
                table: "party_display_grants",
                column: "PartyAlbumLinkId");

            migrationBuilder.CreateIndex(
                name: "ix_party_display_grants_session",
                table: "party_display_grants",
                column: "TvSessionId");

            migrationBuilder.CreateIndex(
                name: "ux_party_display_grants_token_hash",
                table: "party_display_grants",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_display_grants");
        }
    }
}
