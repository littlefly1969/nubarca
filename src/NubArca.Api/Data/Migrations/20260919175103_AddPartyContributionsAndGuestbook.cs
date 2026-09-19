using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyContributionsAndGuestbook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "GuestbookEnabled",
                table: "party_album_links",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireGuestbookApproval",
                table: "party_album_links",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SlideshowMessagesEnabled",
                table: "party_album_links",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "party_guestbook_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyAlbumLinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    PartyParticipantId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorDisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ModeratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ModeratedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_guestbook_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_party_guestbook_entries_parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_guestbook_entries_party_album_links_PartyAlbumLinkId",
                        column: x => x.PartyAlbumLinkId,
                        principalTable: "party_album_links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_guestbook_entries_party_participants_PartyParticipant~",
                        column: x => x.PartyParticipantId,
                        principalTable: "party_participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_guestbook_entries_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_party_guestbook_entries_OwnerUserId",
                table: "party_guestbook_entries",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_party_guestbook_entries_PartyAlbumLinkId",
                table: "party_guestbook_entries",
                column: "PartyAlbumLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_party_guestbook_entries_PartyParticipantId",
                table: "party_guestbook_entries",
                column: "PartyParticipantId");

            migrationBuilder.CreateIndex(
                name: "ix_party_guestbook_party_created",
                table: "party_guestbook_entries",
                columns: new[] { "PartyId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_party_guestbook_party_status_created",
                table: "party_guestbook_entries",
                columns: new[] { "PartyId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_guestbook_entries");

            migrationBuilder.DropColumn(
                name: "GuestbookEnabled",
                table: "party_album_links");

            migrationBuilder.DropColumn(
                name: "RequireGuestbookApproval",
                table: "party_album_links");

            migrationBuilder.DropColumn(
                name: "SlideshowMessagesEnabled",
                table: "party_album_links");
        }
    }
}
