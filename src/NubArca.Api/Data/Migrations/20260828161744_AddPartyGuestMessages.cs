using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyGuestMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Purely additive, and both new booleans default FALSE because that
            // is the only correct backfill rather than merely the CLR default:
            // no party that already exists asked to review its guests' messages,
            // and no member who already holds a share was granted the delegation
            // to moderate them. A default of true on either would silently
            // change an existing installation's behaviour on deploy.
            migrationBuilder.AddColumn<bool>(
                name: "RequireMessageApproval",
                table: "party_album_links",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManagePartyMessages",
                table: "album_memberships",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "party_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyAlbumLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlbumId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyParticipantId = table.Column<Guid>(type: "uuid", nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Body = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ModeratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ModeratedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    HeroPromotedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HeroPromotedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_party_messages_albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_messages_party_album_links_PartyAlbumLinkId",
                        column: x => x.PartyAlbumLinkId,
                        principalTable: "party_album_links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_messages_party_participants_PartyParticipantId",
                        column: x => x.PartyParticipantId,
                        principalTable: "party_participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_messages_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_party_messages_AlbumId",
                table: "party_messages",
                column: "AlbumId");

            migrationBuilder.CreateIndex(
                name: "ix_party_messages_link_hero_promoted",
                table: "party_messages",
                columns: new[] { "PartyAlbumLinkId", "HeroPromotedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_party_messages_link_status_created",
                table: "party_messages",
                columns: new[] { "PartyAlbumLinkId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_party_messages_OwnerUserId",
                table: "party_messages",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_party_messages_PartyParticipantId",
                table: "party_messages",
                column: "PartyParticipantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_messages");

            migrationBuilder.DropColumn(
                name: "RequireMessageApproval",
                table: "party_album_links");

            migrationBuilder.DropColumn(
                name: "CanManagePartyMessages",
                table: "album_memberships");
        }
    }
}
