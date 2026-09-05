using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyGameRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "party_game_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AlbumId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyAlbumLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Phase = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CurrentRoundId = table.Column<Guid>(type: "uuid", nullable: true),
                    CurrentRoundNumber = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_game_sessions", x => x.Id);
                    table.CheckConstraint("ck_party_game_sessions_phase", "\"Phase\" IN ('lobby','challenge_reveal','challenge_active','voting_open','voting_closed','result','finished')");
                    table.CheckConstraint("ck_party_game_sessions_round_number", "\"CurrentRoundNumber\" >= 0");
                    table.CheckConstraint("ck_party_game_sessions_status", "\"Status\" IN ('lobby','live','finished')");
                    table.ForeignKey(
                        name: "FK_party_game_sessions_albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_game_sessions_party_album_links_PartyAlbumLinkId",
                        column: x => x.PartyAlbumLinkId,
                        principalTable: "party_album_links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_game_rounds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyGameSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PhaseStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PhaseEndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_game_rounds", x => x.Id);
                    table.CheckConstraint("ck_party_game_rounds_sequence", "\"Sequence\" >= 1");
                    table.CheckConstraint("ck_party_game_rounds_status", "\"Status\" IN ('active','completed','abandoned')");
                    table.ForeignKey(
                        name: "FK_party_game_rounds_party_challenges_PartyChallengeId",
                        column: x => x.PartyChallengeId,
                        principalTable: "party_challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_game_rounds_party_game_sessions_PartyGameSessionId",
                        column: x => x.PartyGameSessionId,
                        principalTable: "party_game_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_party_game_rounds_PartyChallengeId",
                table: "party_game_rounds",
                column: "PartyChallengeId");

            migrationBuilder.CreateIndex(
                name: "ux_party_game_rounds_session_challenge",
                table: "party_game_rounds",
                columns: new[] { "PartyGameSessionId", "PartyChallengeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_party_game_rounds_session_sequence",
                table: "party_game_rounds",
                columns: new[] { "PartyGameSessionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_party_game_sessions_album",
                table: "party_game_sessions",
                column: "AlbumId");

            migrationBuilder.CreateIndex(
                name: "ux_party_game_sessions_link",
                table: "party_game_sessions",
                column: "PartyAlbumLinkId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_game_rounds");

            migrationBuilder.DropTable(
                name: "party_game_sessions");
        }
    }
}
