using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyChallengeOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_party_game_votes_value",
                table: "party_game_votes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_party_challenges_voting_mode",
                table: "party_challenges");

            migrationBuilder.AlterColumn<string>(
                name: "Value",
                table: "party_game_votes",
                type: "character varying(36)",
                maxLength: 36,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10);

            migrationBuilder.CreateTable(
                name: "party_challenge_options",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_challenge_options", x => x.Id);
                    table.CheckConstraint("ck_party_challenge_options_position", "\"Position\" >= 0 AND \"Position\" <= 5");
                    table.ForeignKey(
                        name: "FK_party_challenge_options_party_challenges_PartyChallengeId",
                        column: x => x.PartyChallengeId,
                        principalTable: "party_challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_game_votes_value",
                table: "party_game_votes",
                sql: "\"Value\" IN ('yes','no') OR length(\"Value\") = 36");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_challenges_voting_mode",
                table: "party_challenges",
                sql: "\"VotingMode\" IN ('none','binary','choice')");

            migrationBuilder.CreateIndex(
                name: "ux_party_challenge_options_position",
                table: "party_challenge_options",
                columns: new[] { "PartyChallengeId", "Position" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_challenge_options");

            migrationBuilder.DropCheckConstraint(
                name: "ck_party_game_votes_value",
                table: "party_game_votes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_party_challenges_voting_mode",
                table: "party_challenges");

            migrationBuilder.AlterColumn<string>(
                name: "Value",
                table: "party_game_votes",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(36)",
                oldMaxLength: 36);

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_game_votes_value",
                table: "party_game_votes",
                sql: "\"Value\" IN ('yes','no')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_challenges_voting_mode",
                table: "party_challenges",
                sql: "\"VotingMode\" IN ('none','binary')");
        }
    }
}
