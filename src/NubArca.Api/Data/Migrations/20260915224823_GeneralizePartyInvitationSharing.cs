using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class GeneralizePartyInvitationSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_party_invitation_deliveries_status",
                table: "party_invitation_deliveries");

            migrationBuilder.AddColumn<string>(
                name: "SearchText",
                table: "party_invitation_groups",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Channel",
                table: "party_invitation_deliveries",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "email");

            migrationBuilder.AddColumn<string>(
                name: "SearchText",
                table: "party_guests",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SearchText",
                table: "party_attendance_guests",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_invitation_deliveries_channel",
                table: "party_invitation_deliveries",
                sql: "\"Channel\" IN ('email', 'whatsapp', 'copy')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_invitation_deliveries_channel_kind",
                table: "party_invitation_deliveries",
                sql: "\"Channel\" = 'email' OR \"Kind\" <> 'reminder'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_invitation_deliveries_channel_status",
                table: "party_invitation_deliveries",
                sql: "(\"Channel\" = 'email') = (\"Status\" IN ('pending', 'sent', 'failed'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_invitation_deliveries_status",
                table: "party_invitation_deliveries",
                sql: "\"Status\" IN ('pending', 'sent', 'failed', 'shared')");

            migrationBuilder.CreateIndex(
                name: "ix_party_attendance_guests_party_checked_in",
                table: "party_attendance_guests",
                columns: new[] { "PartyId", "CheckedInAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_party_invitation_deliveries_channel",
                table: "party_invitation_deliveries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_party_invitation_deliveries_channel_kind",
                table: "party_invitation_deliveries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_party_invitation_deliveries_channel_status",
                table: "party_invitation_deliveries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_party_invitation_deliveries_status",
                table: "party_invitation_deliveries");

            migrationBuilder.DropIndex(
                name: "ix_party_attendance_guests_party_checked_in",
                table: "party_attendance_guests");

            migrationBuilder.DropColumn(
                name: "SearchText",
                table: "party_invitation_groups");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "party_invitation_deliveries");

            migrationBuilder.DropColumn(
                name: "SearchText",
                table: "party_guests");

            migrationBuilder.DropColumn(
                name: "SearchText",
                table: "party_attendance_guests");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_invitation_deliveries_status",
                table: "party_invitation_deliveries",
                sql: "\"Status\" IN ('pending', 'sent', 'failed')");
        }
    }
}
