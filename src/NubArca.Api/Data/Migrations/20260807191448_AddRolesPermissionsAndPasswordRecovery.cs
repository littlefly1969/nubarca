using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <summary>
    /// Identity &amp; Access: role-based authorization with per-user permission
    /// overrides, the richer user profile, session versioning, and hash-only
    /// password-recovery tokens.
    ///
    /// The ORDER of the user-table steps is the migration's whole correctness
    /// argument, and the scaffolder did not produce it: RoleKey is added first,
    /// then backfilled FROM IsAdmin, and only then is IsAdmin dropped. Dropping
    /// the column before reading it — which is what a default scaffold does —
    /// would silently turn every existing administrator into a Member.
    ///
    /// The mapping is total and deterministic:
    ///   IsAdmin = true  → Administrator
    ///   IsAdmin = false → Member  (the column's default, so no update needed)
    /// Member carries every non-administrative feature permission, so an
    /// existing non-admin account keeps exactly the access it had.
    /// </summary>
    public partial class AddRolesPermissionsAndPasswordRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FirstName",
                table: "users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastLoginAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastName",
                table: "users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PasswordChangedAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            // Every existing row lands on Member by the default, which is the
            // correct answer for every previous non-admin.
            migrationBuilder.AddColumn<string>(
                name: "RoleKey",
                table: "users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Member");

            migrationBuilder.AddColumn<int>(
                name: "SecurityVersion",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // …and every previous administrator is promoted here, while IsAdmin
            // still exists to be read.
            migrationBuilder.Sql(
                """
                UPDATE users SET "RoleKey" = 'Administrator' WHERE "IsAdmin" = true;
                """);

            migrationBuilder.DropColumn(
                name: "IsAdmin",
                table: "users");

            migrationBuilder.CreateTable(
                name: "password_reset_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_reset_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_password_reset_tokens_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_permission_overrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermissionKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Effect = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_permission_overrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_permission_overrides_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_RoleKey",
                table: "users",
                column: "RoleKey");

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_tokens_TokenHash",
                table: "password_reset_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_tokens_UserId",
                table: "password_reset_tokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_user_permission_overrides_UserId_PermissionKey",
                table: "user_permission_overrides",
                columns: new[] { "UserId", "PermissionKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "password_reset_tokens");

            migrationBuilder.DropTable(
                name: "user_permission_overrides");

            migrationBuilder.DropIndex(
                name: "IX_users_RoleKey",
                table: "users");

            // Mirror of Up: IsAdmin comes back and is repopulated from RoleKey
            // BEFORE RoleKey is dropped, so a rollback restores the previous
            // administrators rather than demoting everybody.
            migrationBuilder.AddColumn<bool>(
                name: "IsAdmin",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                """
                UPDATE users SET "IsAdmin" = true WHERE "RoleKey" = 'Administrator';
                """);

            migrationBuilder.DropColumn(
                name: "FirstName",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastLoginAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastName",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PasswordChangedAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "RoleKey",
                table: "users");

            migrationBuilder.DropColumn(
                name: "SecurityVersion",
                table: "users");

            migrationBuilder.DropColumn(
                name: "TimeZone",
                table: "users");
        }
    }
}
