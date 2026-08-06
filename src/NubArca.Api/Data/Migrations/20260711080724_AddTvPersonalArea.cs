using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTvPersonalArea : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PersonalPinFailedAttempts",
                table: "tv_sessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PersonalPinLockedUntil",
                table: "tv_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tv_personal_pins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PinHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tv_personal_pins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tv_personal_pins_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tv_personal_unlock_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TvSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    PinGeneration = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tv_personal_unlock_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tv_personal_unlock_grants_tv_sessions_TvSessionId",
                        column: x => x.TvSessionId,
                        principalTable: "tv_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tv_personal_unlock_grants_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_tv_personal_pins_owner",
                table: "tv_personal_pins",
                column: "OwnerUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tv_personal_unlock_grants_OwnerUserId",
                table: "tv_personal_unlock_grants",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "ix_tv_personal_unlock_grants_session",
                table: "tv_personal_unlock_grants",
                column: "TvSessionId");

            migrationBuilder.CreateIndex(
                name: "ux_tv_personal_unlock_grants_token_hash",
                table: "tv_personal_unlock_grants",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tv_personal_pins");

            migrationBuilder.DropTable(
                name: "tv_personal_unlock_grants");

            migrationBuilder.DropColumn(
                name: "PersonalPinFailedAttempts",
                table: "tv_sessions");

            migrationBuilder.DropColumn(
                name: "PersonalPinLockedUntil",
                table: "tv_sessions");
        }
    }
}
