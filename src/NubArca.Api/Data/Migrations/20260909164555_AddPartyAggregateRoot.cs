using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <summary>
    /// Party becomes the root of its own product.
    ///
    /// <para>Three schema facts and one backfill. The tables are new; the one
    /// column added to an existing table is <c>party_album_links.PartyId</c>,
    /// which turns a capability that was scoped to (owner, album) into a
    /// capability OF a party.</para>
    ///
    /// <para><b>The backfill is the interesting half.</b> Every album that has
    /// ever had a party link becomes exactly ONE party with one <c>main</c>
    /// media source — one party per historical party album, however many links
    /// were minted, rotated or revoked on it, because those were all the same
    /// event seen through different QR codes. Media, tokens, participants,
    /// upload provenance, game state, messages, prints, challenges and face
    /// searches are untouched: not one of them is rewritten, re-keyed or
    /// re-issued, and NO RAW TOKEN is read, derived or persisted here — the
    /// stored hashes are exactly the hashes that were already there.</para>
    ///
    /// <para>The status a migrated party takes is what the data actually says.
    /// An album with a live link is <c>published</c>, because a QR for it works
    /// right now. Everything else is <c>draft</c> — nothing is announced at this
    /// moment — rather than <c>ended</c>, which would claim an event took place
    /// and finished, something no row here records.</para>
    ///
    /// <para>The second half gives the five new Party permissions to Member, for
    /// the same reason every feature key before them needed a row: the catalogue
    /// reaches a FRESH installation for free, but the role seeder never rewrites
    /// a built-in role that already exists, so without this every existing
    /// account would silently lose Party while a new one would have it.</para>
    /// </summary>
    public partial class AddPartyAggregateRoot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PartyId",
                table: "party_album_links",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "parties",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "draft"),
                    EventStartsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LiveStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LiveEndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GuestAccessExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LibraryAccessExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_parties_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_media_sources",
                columns: table => new
                {
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlbumId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_media_sources", x => new { x.PartyId, x.AlbumId });
                    table.ForeignKey(
                        name: "FK_party_media_sources_albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_party_media_sources_parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_party_album_links_party",
                table: "party_album_links",
                column: "PartyId");

            migrationBuilder.CreateIndex(
                name: "ix_parties_owner_created",
                table: "parties",
                columns: new[] { "OwnerUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_party_media_sources_album",
                table: "party_media_sources",
                column: "AlbumId");

            migrationBuilder.CreateIndex(
                name: "ix_party_media_sources_party_role",
                table: "party_media_sources",
                columns: new[] { "PartyId", "Role", "SortOrder" });

            // --- BACKFILL: one party per historical party album ---------------
            //
            // Written as one data-modifying CTE so the party id generated for an
            // album is the SAME value the media source and every one of that
            // album's links receive. MATERIALIZED is explicit rather than
            // inferred: gen_random_uuid() must be evaluated once per album, and
            // an inlined CTE would evaluate it again at each reference and hand
            // the three statements three different parties.
            //
            // Ownership comes from the ALBUM, not from the link's projection of
            // it: the album is what the party will resolve to, so if the two ever
            // disagreed the album is the one that decides who the host is.
            migrationBuilder.Sql(
                """
                WITH new_parties AS MATERIALIZED (
                    SELECT
                        a."Id"           AS album_id,
                        a."OwnerUserId"  AS owner_id,
                        a."Name"         AS title,
                        gen_random_uuid() AS party_id,
                        CASE WHEN EXISTS (
                            SELECT 1 FROM party_album_links live
                            WHERE live."AlbumId" = a."Id"
                              AND live."Enabled"
                              AND live."RevokedAt" IS NULL
                              AND (live."ExpiresAt" IS NULL OR live."ExpiresAt" > now())
                        ) THEN 'published' ELSE 'draft' END AS status
                    FROM albums a
                    WHERE EXISTS (
                        SELECT 1 FROM party_album_links l WHERE l."AlbumId" = a."Id")
                ),
                inserted_parties AS (
                    INSERT INTO parties (
                        "Id", "OwnerUserId", "Title", "Description", "Status",
                        "Version", "CreatedAt", "UpdatedAt")
                    SELECT party_id, owner_id, title, NULL, status, 1, now(), now()
                    FROM new_parties
                    RETURNING "Id"
                ),
                inserted_sources AS (
                    INSERT INTO party_media_sources (
                        "PartyId", "AlbumId", "Role", "SortOrder", "CreatedAt")
                    SELECT party_id, album_id, 'main', 0, now()
                    FROM new_parties
                    RETURNING "PartyId"
                )
                UPDATE party_album_links l
                SET "PartyId" = np.party_id
                FROM new_parties np
                WHERE l."AlbumId" = np.album_id;
                """);

            // The zero-GUID default existed only to fill the rows above. Dropping
            // it makes "a link always names a party" a schema fact rather than a
            // value the foreign key happens to reject afterwards.
            migrationBuilder.Sql(
                """ALTER TABLE party_album_links ALTER COLUMN "PartyId" DROP DEFAULT;""");

            migrationBuilder.AddForeignKey(
                name: "FK_party_album_links_parties_PartyId",
                table: "party_album_links",
                column: "PartyId",
                principalTable: "parties",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // --- Party permissions for installations that already exist -------
            //
            // Member only. Administrator is re-synced to the COMPLETE catalogue
            // on every boot, so it gains the keys without being written to here;
            // Restricted is empty by design; and a custom role belongs to the
            // operator — a release that quietly widened one would be a worse
            // defect than a missing capability. The keys are LITERALS rather than
            // references to Permissions, because a migration describes one moment
            // in history and must keep producing the same result after somebody
            // edits a constant.
            migrationBuilder.Sql(
                """
                INSERT INTO role_permissions ("RoleKey", "PermissionKey")
                SELECT 'Member', k.key
                FROM (VALUES
                    ('party.access'),
                    ('party.contributions'),
                    ('party.games'),
                    ('party.print'),
                    ('party.face-search')
                ) AS k(key)
                WHERE EXISTS (SELECT 1 FROM access_roles WHERE "Key" = 'Member')
                  AND NOT EXISTS (
                    SELECT 1 FROM role_permissions
                    WHERE "RoleKey" = 'Member' AND "PermissionKey" = k.key);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the rows this migration added, named explicitly, so a
            // downgrade cannot reach a custom role an operator granted a Party
            // key to afterwards.
            migrationBuilder.Sql(
                """
                DELETE FROM role_permissions
                WHERE "RoleKey" = 'Member'
                  AND "PermissionKey" IN (
                    'party.access', 'party.contributions', 'party.games',
                    'party.print', 'party.face-search');
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_party_album_links_parties_PartyId",
                table: "party_album_links");

            migrationBuilder.DropTable(
                name: "party_media_sources");

            migrationBuilder.DropTable(
                name: "parties");

            migrationBuilder.DropIndex(
                name: "ix_party_album_links_party",
                table: "party_album_links");

            migrationBuilder.DropColumn(
                name: "PartyId",
                table: "party_album_links");
        }
    }
}
