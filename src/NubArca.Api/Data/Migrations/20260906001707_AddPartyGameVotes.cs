using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyGameVotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "party_game_votes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyGameSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyGameRoundId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyParticipantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_game_votes", x => x.Id);
                    table.CheckConstraint("ck_party_game_votes_value", "\"Value\" IN ('yes','no')");
                    table.ForeignKey(
                        name: "FK_party_game_votes_party_game_rounds_PartyGameRoundId",
                        column: x => x.PartyGameRoundId,
                        principalTable: "party_game_rounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_party_game_votes_party_game_sessions_PartyGameSessionId",
                        column: x => x.PartyGameSessionId,
                        principalTable: "party_game_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_party_game_votes_party_participants_PartyParticipantId",
                        column: x => x.PartyParticipantId,
                        principalTable: "party_participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_party_game_votes_PartyGameSessionId",
                table: "party_game_votes",
                column: "PartyGameSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_party_game_votes_PartyParticipantId",
                table: "party_game_votes",
                column: "PartyParticipantId");

            migrationBuilder.CreateIndex(
                name: "ix_party_game_votes_round_value",
                table: "party_game_votes",
                columns: new[] { "PartyGameRoundId", "Value" });

            migrationBuilder.CreateIndex(
                name: "ux_party_game_votes_round_participant",
                table: "party_game_votes",
                columns: new[] { "PartyGameRoundId", "PartyParticipantId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_game_votes");
        }
    }
}
