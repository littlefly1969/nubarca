using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <summary>
    /// Google Cast: the delegated-playback grant table, plus the one role row
    /// that decides who may create one.
    ///
    /// `cast.access` is a NEW capability, so nothing is taken away by adding it
    /// to the catalogue. What the catalogue cannot do on its own is reach an
    /// installation that already exists: the role seeder creates a built-in role
    /// when it is missing and never rewrites one that is present, precisely so an
    /// operator's edits survive a deploy. Member therefore has to be given the
    /// key HERE, once, or every existing account would silently be unable to cast
    /// while a fresh installation could.
    ///
    /// Administrator is deliberately absent from this migration: its rows are
    /// re-synced to the COMPLETE catalogue on every boot, so it gains the key
    /// without being written to here. Restricted is absent because it is empty by
    /// design. Custom roles are absent because they belong to the operator — a
    /// release that quietly widened a role somebody built on purpose would be a
    /// worse defect than the missing capability.
    ///
    /// The role key and the permission key are LITERALS rather than references to
    /// RoleKeys / Permissions. A migration describes one moment in the schema's
    /// history and must keep producing the same result years later; binding it to
    /// a constant a future release may edit would change what this migration did
    /// to databases that ran it long ago.
    /// </summary>
    public partial class AddCastMediaGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cast_media_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cast_media_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cast_media_grants_file_items_FileItemId",
                        column: x => x.FileItemId,
                        principalTable: "file_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_cast_media_grants_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cast_media_grants_expires",
                table: "cast_media_grants",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_cast_media_grants_FileItemId",
                table: "cast_media_grants",
                column: "FileItemId");

            migrationBuilder.CreateIndex(
                name: "ix_cast_media_grants_user_expires",
                table: "cast_media_grants",
                columns: new[] { "UserId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "ux_cast_media_grants_token_hash",
                table: "cast_media_grants",
                column: "TokenHash",
                unique: true);

            // Guarded so re-running against a database that already has the row
            // (a fresh installation seeded from the catalogue, then migrated) is
            // a no-op rather than a unique-constraint failure.
            migrationBuilder.Sql(
                """
                INSERT INTO role_permissions ("RoleKey", "PermissionKey")
                SELECT 'Member', 'cast.access'
                WHERE EXISTS (SELECT 1 FROM access_roles WHERE "Key" = 'Member')
                  AND NOT EXISTS (
                    SELECT 1 FROM role_permissions
                    WHERE "RoleKey" = 'Member' AND "PermissionKey" = 'cast.access');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the row this migration added. A downgrade must not touch a
            // custom role an operator gave the key to afterwards — and it cannot
            // here, because it names Member explicitly.
            migrationBuilder.Sql(
                """
                DELETE FROM role_permissions
                WHERE "RoleKey" = 'Member' AND "PermissionKey" = 'cast.access';
                """);

            migrationBuilder.DropTable(
                name: "cast_media_grants");
        }
    }
}
