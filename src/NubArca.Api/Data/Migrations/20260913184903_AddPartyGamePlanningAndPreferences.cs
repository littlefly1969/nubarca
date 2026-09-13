using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyGamePlanningAndPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_party_game_sessions_phase",
                table: "party_game_sessions");

            migrationBuilder.AddColumn<bool>(
                name: "PriorityVotingEnabled",
                table: "party_album_links",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "party_game_exclusions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyGameSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_game_exclusions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_party_game_exclusions_party_challenges_PartyChallengeId",
                        column: x => x.PartyChallengeId,
                        principalTable: "party_challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_game_exclusions_party_game_sessions_PartyGameSessionId",
                        column: x => x.PartyGameSessionId,
                        principalTable: "party_game_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_game_sessions_phase",
                table: "party_game_sessions",
                sql: "\"Phase\" IN ('lobby','challenge_reveal','challenge_active','voting_open','voting_closed','result','intermission','finished')");

            migrationBuilder.CreateIndex(
                name: "IX_party_game_exclusions_PartyChallengeId",
                table: "party_game_exclusions",
                column: "PartyChallengeId");

            migrationBuilder.CreateIndex(
                name: "ux_party_game_exclusions_session_challenge",
                table: "party_game_exclusions",
                columns: new[] { "PartyGameSessionId", "PartyChallengeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_game_exclusions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_party_game_sessions_phase",
                table: "party_game_sessions");

            migrationBuilder.DropColumn(
                name: "PriorityVotingEnabled",
                table: "party_album_links");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_game_sessions_phase",
                table: "party_game_sessions",
                sql: "\"Phase\" IN ('lobby','challenge_reveal','challenge_active','voting_open','voting_closed','result','finished')");
        }
    }
}
