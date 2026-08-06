using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTvPairingSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tv_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionTokenHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeviceLabel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tv_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tv_sessions_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tv_pairing_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicCode = table.Column<string>(type: "character(8)", fixedLength: true, maxLength: 8, nullable: false),
                    SecretHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TvSessionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tv_pairing_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tv_pairing_requests_tv_sessions_TvSessionId",
                        column: x => x.TvSessionId,
                        principalTable: "tv_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_tv_pairing_requests_users_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tv_pairing_requests_ApprovedByUserId",
                table: "tv_pairing_requests",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_tv_pairing_requests_expires_at",
                table: "tv_pairing_requests",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_tv_pairing_requests_TvSessionId",
                table: "tv_pairing_requests",
                column: "TvSessionId");

            migrationBuilder.CreateIndex(
                name: "ux_tv_pairing_requests_public_code",
                table: "tv_pairing_requests",
                column: "PublicCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tv_sessions_owner_expires",
                table: "tv_sessions",
                columns: new[] { "OwnerUserId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "ux_tv_sessions_token_hash",
                table: "tv_sessions",
                column: "SessionTokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tv_pairing_requests");

            migrationBuilder.DropTable(
                name: "tv_sessions");
        }
    }
}
