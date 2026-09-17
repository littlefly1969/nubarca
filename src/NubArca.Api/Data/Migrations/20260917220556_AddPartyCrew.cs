using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyCrew : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PartyCollaboratorId",
                table: "audit_logs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "party_collaborators",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    RoleKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_collaborators", x => x.Id);
                    table.CheckConstraint("ck_party_collaborators_role", "\"RoleKey\" IN ('co_organizer', 'director', 'dj', 'reception', 'honoree')");
                    table.ForeignKey(
                        name: "FK_party_collaborators_parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_crew_devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DeviceLabel = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_crew_devices", x => x.Id);
                    table.CheckConstraint("ck_party_crew_devices_token_hash", "length(\"TokenHash\") = 64");
                });

            migrationBuilder.CreateTable(
                name: "party_collaborator_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyCollaboratorId = table.Column<Guid>(type: "uuid", nullable: false),
                    CapabilityKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_collaborator_grants", x => x.Id);
                    table.CheckConstraint("ck_party_collaborator_grants_capability", "\"CapabilityKey\" IN ('party-crew.details.manage', 'party-crew.lifecycle.manage', 'party-crew.experience.manage', 'party-crew.guests.read', 'party-crew.invitations.manage', 'party-crew.attendance.manage', 'party-crew.contributions.configure', 'party-crew.contributions.moderate', 'party-crew.activities.manage', 'party-crew.activities.control', 'party-crew.screens.manage', 'party-crew.print.manage')");
                    table.ForeignKey(
                        name: "FK_party_collaborator_grants_party_collaborators_PartyCollabor~",
                        column: x => x.PartyCollaboratorId,
                        principalTable: "party_collaborators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_collaborator_invites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyCollaboratorId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_collaborator_invites", x => x.Id);
                    table.CheckConstraint("ck_party_collaborator_invites_token_hash", "length(\"TokenHash\") = 64");
                    table.ForeignKey(
                        name: "FK_party_collaborator_invites_party_collaborators_PartyCollabo~",
                        column: x => x.PartyCollaboratorId,
                        principalTable: "party_collaborators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_collaborator_device_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyCrewDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyCollaboratorId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_collaborator_device_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_party_collaborator_device_grants_party_collaborators_PartyC~",
                        column: x => x.PartyCollaboratorId,
                        principalTable: "party_collaborators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_collaborator_device_grants_party_crew_devices_PartyCr~",
                        column: x => x.PartyCrewDeviceId,
                        principalTable: "party_crew_devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_collaborator_auth_challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyCollaboratorId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyCollaboratorInviteId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OtpProof = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OtpGeneration = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OtpSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OtpSendCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_collaborator_auth_challenges", x => x.Id);
                    table.CheckConstraint("ck_party_collaborator_auth_challenges_attempts", "\"FailedAttempts\" >= 0");
                    table.CheckConstraint("ck_party_collaborator_auth_challenges_generation", "\"OtpGeneration\" >= 1");
                    table.CheckConstraint("ck_party_collaborator_auth_challenges_otp_proof", "length(\"OtpProof\") = 64");
                    table.CheckConstraint("ck_party_collaborator_auth_challenges_sends", "\"OtpSendCount\" >= 0");
                    table.CheckConstraint("ck_party_collaborator_auth_challenges_token_hash", "length(\"ChallengeTokenHash\") = 64");
                    table.ForeignKey(
                        name: "FK_party_collaborator_auth_challenges_party_collaborator_invit~",
                        column: x => x.PartyCollaboratorInviteId,
                        principalTable: "party_collaborator_invites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_collaborator_auth_challenges_party_collaborators_Part~",
                        column: x => x.PartyCollaboratorId,
                        principalTable: "party_collaborators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_collaborator_created",
                table: "audit_logs",
                columns: new[] { "PartyCollaboratorId", "CreatedAt" },
                filter: "\"PartyCollaboratorId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_party_collaborator_auth_challenges_collaborator",
                table: "party_collaborator_auth_challenges",
                columns: new[] { "PartyCollaboratorId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_party_collaborator_auth_challenges_PartyCollaboratorInviteId",
                table: "party_collaborator_auth_challenges",
                column: "PartyCollaboratorInviteId");

            migrationBuilder.CreateIndex(
                name: "ux_party_collaborator_auth_challenges_token_hash",
                table: "party_collaborator_auth_challenges",
                column: "ChallengeTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_party_collaborator_device_grants_collaborator_revoked",
                table: "party_collaborator_device_grants",
                columns: new[] { "PartyCollaboratorId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_party_collaborator_device_grants_device_collaborator",
                table: "party_collaborator_device_grants",
                columns: new[] { "PartyCrewDeviceId", "PartyCollaboratorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_party_collaborator_grants_collaborator_capability",
                table: "party_collaborator_grants",
                columns: new[] { "PartyCollaboratorId", "CapabilityKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_party_collaborator_invites_collaborator",
                table: "party_collaborator_invites",
                column: "PartyCollaboratorId");

            migrationBuilder.CreateIndex(
                name: "ux_party_collaborator_invites_token_hash",
                table: "party_collaborator_invites",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_party_collaborators_party_created",
                table: "party_collaborators",
                columns: new[] { "PartyId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_party_collaborators_party_email_live",
                table: "party_collaborators",
                columns: new[] { "PartyId", "NormalizedEmail" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_party_crew_devices_token_hash",
                table: "party_crew_devices",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_collaborator_auth_challenges");

            migrationBuilder.DropTable(
                name: "party_collaborator_device_grants");

            migrationBuilder.DropTable(
                name: "party_collaborator_grants");

            migrationBuilder.DropTable(
                name: "party_collaborator_invites");

            migrationBuilder.DropTable(
                name: "party_crew_devices");

            migrationBuilder.DropTable(
                name: "party_collaborators");

            migrationBuilder.DropIndex(
                name: "ix_audit_logs_collaborator_created",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "PartyCollaboratorId",
                table: "audit_logs");
        }
    }
}
