using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <summary>
    /// `people.cluster.rebuild` for installations that already exist.
    ///
    /// No schema: one role_permissions row. The permission is new, so nothing is
    /// taken away by adding it to the catalogue — but the catalogue alone cannot
    /// reach a database that is already running. The role seeder creates a
    /// built-in role when it is MISSING and never rewrites one that is present,
    /// precisely so an operator's edits survive a deploy; Member therefore has to
    /// be given the key here, once, or every existing account would silently be
    /// unable to rebuild its own face groups while a fresh installation could.
    ///
    /// Administrator is deliberately absent: its rows are re-synced to the
    /// COMPLETE catalogue on every boot, so it gains the key without being
    /// written to here. Restricted is absent because it is empty by design.
    /// Custom roles are absent because they belong to the operator — a release
    /// that quietly widened a role somebody built on purpose would be a worse
    /// defect than the missing capability.
    ///
    /// The role key and the permission key are LITERALS rather than references to
    /// RoleKeys / Permissions. A migration describes one moment in the schema's
    /// history and must keep producing the same result years later; binding it to
    /// a constant a future release may edit would change what this migration did
    /// to databases that ran it long ago.
    /// </summary>
    public partial class AddPeopleClusterRebuildPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guarded so re-running against a database that already has the row
            // (a fresh installation seeded from the catalogue, then migrated) is
            // a no-op rather than a unique-constraint failure.
            migrationBuilder.Sql(
                """
                INSERT INTO role_permissions ("RoleKey", "PermissionKey")
                SELECT 'Member', 'people.cluster.rebuild'
                WHERE EXISTS (SELECT 1 FROM access_roles WHERE "Key" = 'Member')
                  AND NOT EXISTS (
                    SELECT 1 FROM role_permissions
                    WHERE "RoleKey" = 'Member' AND "PermissionKey" = 'people.cluster.rebuild');
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
                WHERE "RoleKey" = 'Member' AND "PermissionKey" = 'people.cluster.rebuild';
                """);
        }
    }
}
