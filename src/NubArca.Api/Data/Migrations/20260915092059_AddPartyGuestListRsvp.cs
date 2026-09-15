using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyGuestListRsvp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "party_invitation_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    RecipientEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    MaxAdditionalGuests = table.Column<int>(type: "integer", nullable: false),
                    CapabilityId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CapabilityIssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_invitation_groups", x => x.Id);
                    table.CheckConstraint("ck_party_invitation_groups_max_additional", "\"MaxAdditionalGuests\" BETWEEN 0 AND 10");
                    table.CheckConstraint("ck_party_invitation_groups_token_hash", "length(\"TokenHash\") = 64");
                    table.ForeignKey(
                        name: "FK_party_invitation_groups_parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_rsvp_questions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Prompt = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    OptionsJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_rsvp_questions", x => x.Id);
                    table.CheckConstraint("ck_party_rsvp_questions_kind", "\"Kind\" IN ('short_text', 'single_choice', 'yes_no')");
                    table.CheckConstraint("ck_party_rsvp_questions_options", "(\"Kind\" = 'single_choice') = (\"OptionsJson\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_party_rsvp_questions_parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_guests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyInvitationGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    IsAdditionalGuest = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_guests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_party_guests_party_invitation_groups_PartyInvitationGroupId",
                        column: x => x.PartyInvitationGroupId,
                        principalTable: "party_invitation_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_invitation_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyInvitationGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    CapabilityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_invitation_deliveries", x => x.Id);
                    table.CheckConstraint("ck_party_invitation_deliveries_completion", "(\"Status\" = 'pending') = (\"CompletedAt\" IS NULL)");
                    table.CheckConstraint("ck_party_invitation_deliveries_kind", "\"Kind\" IN ('initial', 'resend', 'reminder')");
                    table.CheckConstraint("ck_party_invitation_deliveries_status", "\"Status\" IN ('pending', 'sent', 'failed')");
                    table.ForeignKey(
                        name: "FK_party_invitation_deliveries_party_invitation_groups_PartyIn~",
                        column: x => x.PartyInvitationGroupId,
                        principalTable: "party_invitation_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_rsvp_answers",
                columns: table => new
                {
                    PartyInvitationGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyRsvpQuestionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ValueJson = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_rsvp_answers", x => new { x.PartyInvitationGroupId, x.PartyRsvpQuestionId });
                    table.ForeignKey(
                        name: "FK_party_rsvp_answers_party_invitation_groups_PartyInvitationG~",
                        column: x => x.PartyInvitationGroupId,
                        principalTable: "party_invitation_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_rsvp_answers_party_rsvp_questions_PartyRsvpQuestionId",
                        column: x => x.PartyRsvpQuestionId,
                        principalTable: "party_rsvp_questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_rsvps",
                columns: table => new
                {
                    PartyGuestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "pending"),
                    DietaryNotes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RespondedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_rsvps", x => x.PartyGuestId);
                    table.CheckConstraint("ck_party_rsvps_status", "\"Status\" IN ('pending', 'attending', 'declined')");
                    table.ForeignKey(
                        name: "FK_party_rsvps_party_guests_PartyGuestId",
                        column: x => x.PartyGuestId,
                        principalTable: "party_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_party_guests_group_order",
                table: "party_guests",
                columns: new[] { "PartyInvitationGroupId", "IsAdditionalGuest", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "ix_party_invitation_deliveries_group_created",
                table: "party_invitation_deliveries",
                columns: new[] { "PartyInvitationGroupId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_party_invitation_deliveries_request",
                table: "party_invitation_deliveries",
                columns: new[] { "PartyInvitationGroupId", "ClientRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_party_invitation_groups_party_created",
                table: "party_invitation_groups",
                columns: new[] { "PartyId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_party_invitation_groups_token_hash",
                table: "party_invitation_groups",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_party_rsvp_answers_question",
                table: "party_rsvp_answers",
                column: "PartyRsvpQuestionId");

            migrationBuilder.CreateIndex(
                name: "ix_party_rsvp_questions_party_order",
                table: "party_rsvp_questions",
                columns: new[] { "PartyId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_invitation_deliveries");

            migrationBuilder.DropTable(
                name: "party_rsvp_answers");

            migrationBuilder.DropTable(
                name: "party_rsvps");

            migrationBuilder.DropTable(
                name: "party_rsvp_questions");

            migrationBuilder.DropTable(
                name: "party_guests");

            migrationBuilder.DropTable(
                name: "party_invitation_groups");
        }
    }
}
