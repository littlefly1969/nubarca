using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyGuestHubChallenges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChallengeVoteCount",
                table: "party_participants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "GameEnabled",
                table: "party_album_links",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MaxChallengeIntervalSeconds",
                table: "party_album_links",
                type: "integer",
                nullable: false,
                defaultValue: 540);

            migrationBuilder.AddColumn<int>(
                name: "MaxChallengesPerSession",
                table: "party_album_links",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinChallengeIntervalSeconds",
                table: "party_album_links",
                type: "integer",
                nullable: false,
                defaultValue: 300);

            migrationBuilder.AddColumn<int>(
                name: "VotesPerGuest",
                table: "party_album_links",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.CreateTable(
                name: "party_challenge_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyAlbumLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActiveChallengeId = table.Column<Guid>(type: "uuid", nullable: true),
                    NextChallengeAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedCount = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_challenge_sessions", x => x.Id);
                    table.CheckConstraint("ck_party_challenge_sessions_mode", "\"Mode\" IN ('media','challenge_hold')");
                    table.ForeignKey(
                        name: "FK_party_challenge_sessions_party_album_links_PartyAlbumLinkId",
                        column: x => x.PartyAlbumLinkId,
                        principalTable: "party_album_links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AlbumId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Body = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MediaFileItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_challenges", x => x.Id);
                    table.CheckConstraint("ck_party_challenges_kind", "\"Kind\" IN ('dare','penalty','guess','custom')");
                    table.CheckConstraint("ck_party_challenges_sort_order", "\"SortOrder\" >= 0");
                    table.ForeignKey(
                        name: "FK_party_challenges_albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_challenge_completions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyAlbumLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_challenge_completions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_party_challenge_completions_party_album_links_PartyAlbumLin~",
                        column: x => x.PartyAlbumLinkId,
                        principalTable: "party_album_links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_challenge_completions_party_challenges_PartyChallenge~",
                        column: x => x.PartyChallengeId,
                        principalTable: "party_challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_challenge_votes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyAlbumLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyParticipantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_challenge_votes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_party_challenge_votes_party_album_links_PartyAlbumLinkId",
                        column: x => x.PartyAlbumLinkId,
                        principalTable: "party_album_links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_challenge_votes_party_challenges_PartyChallengeId",
                        column: x => x.PartyChallengeId,
                        principalTable: "party_challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_challenge_votes_party_participants_PartyParticipantId",
                        column: x => x.PartyParticipantId,
                        principalTable: "party_participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_party_challenge_completions_PartyChallengeId",
                table: "party_challenge_completions",
                column: "PartyChallengeId");

            migrationBuilder.CreateIndex(
                name: "ux_party_challenge_completions_link_challenge",
                table: "party_challenge_completions",
                columns: new[] { "PartyAlbumLinkId", "PartyChallengeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_party_challenge_sessions_link",
                table: "party_challenge_sessions",
                column: "PartyAlbumLinkId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_party_challenge_votes_link_challenge",
                table: "party_challenge_votes",
                columns: new[] { "PartyAlbumLinkId", "PartyChallengeId" });

            migrationBuilder.CreateIndex(
                name: "IX_party_challenge_votes_PartyChallengeId",
                table: "party_challenge_votes",
                column: "PartyChallengeId");

            migrationBuilder.CreateIndex(
                name: "IX_party_challenge_votes_PartyParticipantId",
                table: "party_challenge_votes",
                column: "PartyParticipantId");

            migrationBuilder.CreateIndex(
                name: "ux_party_challenge_votes_link_guest_challenge",
                table: "party_challenge_votes",
                columns: new[] { "PartyAlbumLinkId", "PartyParticipantId", "PartyChallengeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_party_challenges_album_order",
                table: "party_challenges",
                columns: new[] { "AlbumId", "SortOrder", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_challenge_completions");

            migrationBuilder.DropTable(
                name: "party_challenge_sessions");

            migrationBuilder.DropTable(
                name: "party_challenge_votes");

            migrationBuilder.DropTable(
                name: "party_challenges");

            migrationBuilder.DropColumn(
                name: "ChallengeVoteCount",
                table: "party_participants");

            migrationBuilder.DropColumn(
                name: "GameEnabled",
                table: "party_album_links");

            migrationBuilder.DropColumn(
                name: "MaxChallengeIntervalSeconds",
                table: "party_album_links");

            migrationBuilder.DropColumn(
                name: "MaxChallengesPerSession",
                table: "party_album_links");

            migrationBuilder.DropColumn(
                name: "MinChallengeIntervalSeconds",
                table: "party_album_links");

            migrationBuilder.DropColumn(
                name: "VotesPerGuest",
                table: "party_album_links");
        }
    }
}
