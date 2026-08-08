using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <summary>
    /// Roles become first-class rows and per-user permission exceptions are
    /// removed.
    ///
    /// The ORDER is the migration's correctness argument, and the scaffolder did
    /// not produce it: it dropped user_permission_overrides FIRST, which would
    /// have silently discarded every exception an operator had ever set. Here
    /// the role tables are created and seeded, the overrides are READ and turned
    /// into real roles, and only then is the table dropped.
    ///
    /// The promise is exact: for every account, the set of permissions in force
    /// after this migration equals the set that was in force before it. A user
    /// with no exception keeps their role. A user with exceptions is moved to a
    /// role that carries precisely their old effective set — an existing role
    /// when one already matches, otherwise a new "Migrated access N" role, with
    /// one role reused for every user who resolved to the same set.
    ///
    /// The permission keys are HARD-CODED here rather than read from
    /// PermissionCatalog. A migration describes a moment in the schema's history
    /// and must keep producing the same result years later; binding it to a
    /// constant that a future release edits would silently change what this
    /// migration did to databases that ran it long ago.
    /// </summary>
    public partial class MakeRolesFirstClass : Migration
    {
        // The catalogue as it stands at this migration: eight feature
        // permissions plus five administrative ones.
        private const string FeatureKeys =
            "'people.access','semantic-search.access','laboratory.access'," +
            "'laboratory.plates','laboratory.aesthetics','cloud-functions.access'," +
            "'private-vault.access','tv.manage'";

        private const string AdministrativeKeys =
            "'admin.dashboard','admin.users.manage','admin.import'," +
            "'admin.jobs.manage','admin.roles.manage'";

        private const string AllKeys = FeatureKeys + "," + AdministrativeKeys;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The column was sized for the three built-in names. It is a
            // foreign key into access_roles now, and a custom role's generated
            // `custom:<uuid>` key is 39 characters — so this has to widen
            // BEFORE any custom key is written to it.
            migrationBuilder.AlterColumn<string>(
                name: "RoleKey",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Member",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldDefaultValue: "Member");

            migrationBuilder.CreateTable(
                name: "access_roles",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    IsAdministrator = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_roles", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                columns: table => new
                {
                    RoleKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PermissionKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permissions", x => new { x.RoleKey, x.PermissionKey });
                    table.ForeignKey(
                        name: "FK_role_permissions_access_roles_RoleKey",
                        column: x => x.RoleKey,
                        principalTable: "access_roles",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            // The data steps are PostgreSQL-only, like the rest of the migration
            // set: PL/pgSQL is not SQLite, and the SQLite-backed tests build
            // their schema with EnsureCreated and seed the roles through
            // RoleService. Guarded rather than assumed, so a non-Npgsql provider
            // skips them instead of failing.
            if (IsNpgsql(migrationBuilder))
            {
            // 1. The three built-in roles, with the permission sets they have on
            //    a fresh installation. Member is deliberately every
            //    non-administrative feature: that is what every pre-role account
            //    became, and the promise made to those accounts.
            migrationBuilder.Sql(
                $"""
                INSERT INTO access_roles
                    ("Key", "Name", "Description", "IsSystem", "IsAdministrator",
                     "CreatedAt", "UpdatedAt", "Version")
                VALUES
                    ('Administrator', 'Administrator',
                     'Full control of NubArca, including users and roles.', true, true, now(), now(), 1),
                    ('Member', 'Member',
                     'Standard access to NubArca advanced features.', true, false, now(), now(), 1),
                    ('Restricted', 'Restricted',
                     'Files, media, albums, sharing and trash only.', true, false, now(), now(), 1);

                INSERT INTO role_permissions ("RoleKey", "PermissionKey")
                SELECT 'Administrator', k FROM unnest(ARRAY[{AllKeys}]) AS k;

                INSERT INTO role_permissions ("RoleKey", "PermissionKey")
                SELECT 'Member', k FROM unnest(ARRAY[{FeatureKeys}]) AS k;
                """);

            // 2. Any role value the old code did not recognise resolved to the
            //    Member baseline at runtime, so Member is the exact-preservation
            //    answer — and the foreign key added in step 4 needs every value
            //    to name a real role.
            migrationBuilder.Sql(
                """
                UPDATE users SET "RoleKey" = 'Member'
                WHERE "RoleKey" NOT IN ('Administrator', 'Member', 'Restricted');
                """);

            // 3. The exceptions themselves, turned into roles. Read BEFORE the
            //    table is dropped, which is the whole point of the ordering.
            migrationBuilder.Sql(MigrateOverridesSql());
            }

            migrationBuilder.AddForeignKey(
                name: "FK_users_access_roles_RoleKey",
                table: "users",
                column: "RoleKey",
                principalTable: "access_roles",
                principalColumn: "Key",
                onDelete: ReferentialAction.Restrict);

            // 4. Only now, with every exception represented as a role, does the
            //    old table stop describing anything.
            migrationBuilder.DropTable(name: "user_permission_overrides");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
                name: "IX_user_permission_overrides_UserId_PermissionKey",
                table: "user_permission_overrides",
                columns: new[] { "UserId", "PermissionKey" },
                unique: true);

            if (IsNpgsql(migrationBuilder))
            {
                // The mirror of Up, and it is exact for what Up produced: every
                // user on a custom role goes back to Member carrying the grants
                // and denies that reproduce their permission set precisely.
                //
                // One thing a rollback CANNOT represent, because the old model
                // had nowhere to put it: an edit an operator made to the Member
                // or Restricted permission set itself. Those roles return to the
                // baselines the old code hard-coded.
                migrationBuilder.Sql(RestoreOverridesSql());
            }

            migrationBuilder.DropForeignKey(
                name: "FK_users_access_roles_RoleKey",
                table: "users");

            migrationBuilder.DropTable(name: "role_permissions");
            migrationBuilder.DropTable(name: "access_roles");

            // Narrow again only AFTER every custom key is gone — the rollback
            // above put those users back on `Member`.
            migrationBuilder.AlterColumn<string>(
                name: "RoleKey",
                table: "users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Member",
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldDefaultValue: "Member");
        }

        // Every user who had an exception is placed on a role carrying exactly
        // the permissions that were in force for them.
        //
        // Two keys are dropped while computing the set, and neither changes what
        // anybody could actually do:
        //
        //   * `admin.roles.manage` did not exist before this release, so no
        //     override can name it; excluding it keeps the invariant that only
        //     the Administrator role carries it true by construction.
        //   * A Laboratory section without the Laboratory shell opened nothing —
        //     the endpoint policy required both — so an orphaned child key is
        //     removed rather than migrated into a role that would look like it
        //     granted something.
        //
        // Administrators are skipped: the old resolver ignored their overrides
        // entirely, so their effective set was already the whole catalogue and
        // the Administrator role is the exact answer.
        private static string MigrateOverridesSql() =>
            $"""
            DO $migrate$
            DECLARE
                v_user     RECORD;
                v_set      text[];
                v_role     text;
                v_sequence int := 0;
            BEGIN
                FOR v_user IN
                    SELECT u."Id" AS id, u."RoleKey" AS role_key
                    FROM users u
                    WHERE u."RoleKey" <> 'Administrator'
                      AND EXISTS (
                          SELECT 1 FROM user_permission_overrides o WHERE o."UserId" = u."Id")
                    ORDER BY u."Email"
                LOOP
                    -- Every aggregate here is cast to text[]. The permission
                    -- columns are character varying, and PostgreSQL has no
                    -- equality operator between varchar[] and text[] — so the
                    -- role-matching comparison below fails outright without it.
                    SELECT COALESCE(array_agg(effective.k::text ORDER BY effective.k), ARRAY[]::text[])
                    INTO v_set
                    FROM (
                        SELECT rp."PermissionKey" AS k
                        FROM role_permissions rp
                        WHERE rp."RoleKey" = v_user.role_key
                        UNION
                        SELECT o."PermissionKey"
                        FROM user_permission_overrides o
                        WHERE o."UserId" = v_user.id AND o."Effect" = 'Grant'
                    ) AS effective
                    WHERE effective.k IN ({FeatureKeys},'admin.dashboard','admin.users.manage','admin.import','admin.jobs.manage')
                      AND NOT EXISTS (
                          SELECT 1 FROM user_permission_overrides d
                          WHERE d."UserId" = v_user.id
                            AND d."PermissionKey" = effective.k
                            AND d."Effect" = 'Deny');

                    -- A section key without its shell granted nothing.
                    IF NOT ('laboratory.access' = ANY (v_set)) THEN
                        SELECT COALESCE(array_agg(k::text ORDER BY k), ARRAY[]::text[]) INTO v_set
                        FROM unnest(v_set) AS k
                        WHERE k NOT IN ('laboratory.plates', 'laboratory.aesthetics');
                    END IF;

                    -- Reuse a role that already means exactly this — an existing
                    -- built-in, or one created earlier in this same loop, so two
                    -- users with identical access share one role rather than
                    -- getting a private copy each.
                    SELECT r."Key" INTO v_role
                    FROM access_roles r
                    WHERE r."IsAdministrator" = false
                      AND COALESCE(
                            (SELECT array_agg(rp."PermissionKey"::text ORDER BY rp."PermissionKey")
                             FROM role_permissions rp WHERE rp."RoleKey" = r."Key"),
                            ARRAY[]::text[]) = v_set
                    ORDER BY r."IsSystem" DESC, r."Key"
                    LIMIT 1;

                    IF v_role IS NULL THEN
                        v_sequence := v_sequence + 1;
                        v_role := 'custom:' || replace(gen_random_uuid()::text, '-', '');
                        INSERT INTO access_roles
                            ("Key", "Name", "Description", "IsSystem", "IsAdministrator",
                             "CreatedAt", "UpdatedAt", "Version")
                        VALUES
                            (v_role, 'Migrated access ' || v_sequence,
                             'Created automatically from the previous per-user permission model.',
                             false, false, now(), now(), 1);

                        INSERT INTO role_permissions ("RoleKey", "PermissionKey")
                        SELECT v_role, k FROM unnest(v_set) AS k;

                        RAISE NOTICE 'MakeRolesFirstClass: created role % with % permission(s)',
                            v_role, coalesce(array_length(v_set, 1), 0);
                    END IF;

                    UPDATE users SET "RoleKey" = v_role WHERE "Id" = v_user.id;
                    v_role := NULL;
                END LOOP;
            END
            $migrate$;
            """;

        private static string RestoreOverridesSql() =>
            $"""
            DO $rollback$
            DECLARE
                v_user     RECORD;
                v_set      text[];
                v_baseline text[] := ARRAY[{FeatureKeys}];
            BEGIN
                FOR v_user IN
                    SELECT u."Id" AS id, u."RoleKey" AS role_key
                    FROM users u
                    JOIN access_roles r ON r."Key" = u."RoleKey"
                    WHERE r."IsSystem" = false
                LOOP
                    SELECT COALESCE(array_agg(rp."PermissionKey"::text ORDER BY rp."PermissionKey"),
                                    ARRAY[]::text[])
                    INTO v_set
                    FROM role_permissions rp
                    WHERE rp."RoleKey" = v_user.role_key;

                    INSERT INTO user_permission_overrides
                        ("Id", "UserId", "PermissionKey", "Effect", "CreatedAt", "UpdatedAt")
                    SELECT gen_random_uuid(), v_user.id, k, 'Grant', now(), now()
                    FROM unnest(v_set) AS k
                    WHERE NOT (k = ANY (v_baseline));

                    INSERT INTO user_permission_overrides
                        ("Id", "UserId", "PermissionKey", "Effect", "CreatedAt", "UpdatedAt")
                    SELECT gen_random_uuid(), v_user.id, k, 'Deny', now(), now()
                    FROM unnest(v_baseline) AS k
                    WHERE NOT (k = ANY (v_set));

                    UPDATE users SET "RoleKey" = 'Member' WHERE "Id" = v_user.id;
                END LOOP;
            END
            $rollback$;
            """;

        private static bool IsNpgsql(MigrationBuilder migrationBuilder) =>
            migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }
}
