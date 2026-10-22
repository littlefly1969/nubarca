using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <summary>
    /// `mail.access` and `document-search.access` for installations that
    /// already exist.
    ///
    /// No schema: two role_permissions rows. Before this release MailArchive and
    /// the document assistant/search answered every signed-in user; they are now
    /// gated. Member keeps both, as it keeps every feature a normal NubArca user
    /// already had, so nobody on Member loses mail or documents on upgrade. The
    /// role seeder never rewrites a built-in role that is present, so Member
    /// must be given the keys here, once.
    ///
    /// Administrator is absent: its rows are re-synced to the complete catalogue
    /// on every boot. Restricted is absent because it is "files, media, albums,
    /// sharing and trash only" by design. Custom roles are absent because they
    /// belong to the operator, who grants the keys in the role editor.
    ///
    /// Role and permission keys are LITERALS, so this migration keeps doing what
    /// it did whatever a later release does to the constants.
    /// </summary>
    public partial class AddMailAndDocumentSearchPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guarded so a database already seeded with the rows (a fresh
            // installation seeded from the catalogue, then migrated) is a no-op.
            migrationBuilder.Sql(
                """
                INSERT INTO role_permissions ("RoleKey", "PermissionKey")
                SELECT 'Member', k.key
                FROM (VALUES ('mail.access'), ('document-search.access')) AS k(key)
                WHERE EXISTS (SELECT 1 FROM access_roles WHERE "Key" = 'Member')
                  AND NOT EXISTS (
                    SELECT 1 FROM role_permissions
                    WHERE "RoleKey" = 'Member' AND "PermissionKey" = k.key);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately nothing. Up() is guarded, so a row present before it
            // ran (a pre-seeded database) or granted afterwards by an operator
            // cannot be told apart from one Up() inserted; deleting by key would
            // take those too. Leaving the rows is safe: the previous application
            // does not know either key, and a role row naming a key outside the
            // catalogue is inert there. Re-applying Up() later is a no-op.
        }
    }
}
