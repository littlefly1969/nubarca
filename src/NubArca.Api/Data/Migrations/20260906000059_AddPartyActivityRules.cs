using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyActivityRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DurationSeconds",
                table: "party_challenges",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoteQuestion",
                table: "party_challenges",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VotingMode",
                table: "party_challenges",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "binary");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_challenges_duration",
                table: "party_challenges",
                sql: "\"DurationSeconds\" IS NULL OR (\"DurationSeconds\" >= 5 AND \"DurationSeconds\" <= 3600)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_challenges_voting_mode",
                table: "party_challenges",
                sql: "\"VotingMode\" IN ('none','binary')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_party_challenges_duration",
                table: "party_challenges");

            migrationBuilder.DropCheckConstraint(
                name: "ck_party_challenges_voting_mode",
                table: "party_challenges");

            migrationBuilder.DropColumn(
                name: "DurationSeconds",
                table: "party_challenges");

            migrationBuilder.DropColumn(
                name: "VoteQuestion",
                table: "party_challenges");

            migrationBuilder.DropColumn(
                name: "VotingMode",
                table: "party_challenges");
        }
    }
}
