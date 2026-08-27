using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <summary>
    /// Moves the authoritative snapshot revision from the SOURCE row to the
    /// DOMAIN MEMBERSHIP row, and makes source identity content-shaped.
    ///
    /// Before: one `rag_sources` row per SourceKey, owning the bytes, the chunks
    /// AND the revision. That single ownership is what deadlocked the release
    /// lifecycle — advancing `nubarca-repository` from commit A to commit B
    /// rewrote the row `product-help` was serving at A, and Help could not go
    /// first for the same reason.
    ///
    /// After: a source row is one CONTENT INTERPRETATION,
    /// (SourceKey, ContentHash, IndexFormatVersion), and each membership says
    /// which revision ITS domain is using that content at. Two domains sharing an
    /// unchanged file are one source row and two memberships that may sit at two
    /// different revisions while they upgrade one at a time.
    ///
    /// The interesting part of this file is the BACKFILL, not the DDL. Every
    /// existing membership inherits the revision of the source it points at
    /// before that column is dropped; a scaffolded version of this migration
    /// drops it first and leaves every membership at the empty string, which
    /// retrieval reads as "no coherent revision" and refuses — a silent, total
    /// outage of an index that was perfectly good. There is exactly one moment
    /// when that provenance exists in the database, and it is here.
    ///
    /// A source row nothing points at loses its revision, because there is
    /// nowhere to put it. Such a row is already unreachable — the indexer deletes
    /// content the moment its last membership leaves — so nothing retrievable is
    /// affected.
    /// </summary>
    public partial class MoveRagRevisionToDomainMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Added first, and empty, so the backfill has a destination. NOT NULL
            // with a default is deliberate: a nullable column would make "no
            // revision" and "revision not yet copied" the same value forever.
            migrationBuilder.AddColumn<string>(
                name: "Revision",
                table: "rag_domain_sources",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // THE PROVENANCE COPY. Every membership adopts the revision of the
            // source it is a membership OF, which is exactly what it meant
            // before: one row per key, so a domain's revision WAS its source's
            // revision. This is the last instant that mapping exists.
            if (IsNpgsql(migrationBuilder))
            {
                migrationBuilder.Sql(
                    """
                    UPDATE rag_domain_sources m
                    SET "Revision" = s."Revision"
                    FROM rag_sources s
                    WHERE s."Id" = m."SourceId";
                    """);
            }

            migrationBuilder.DropIndex(
                name: "ix_rag_sources_revision",
                table: "rag_sources");

            migrationBuilder.DropIndex(
                name: "ux_rag_sources_key",
                table: "rag_sources");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "rag_sources");

            // SourceKey is now a LOOKUP key, not an identity: the indexer asks
            // for every content row a key has while two domains disagree.
            migrationBuilder.CreateIndex(
                name: "ix_rag_sources_key",
                table: "rag_sources",
                column: "SourceKey");

            // Content identity. Satisfiable on existing data by construction —
            // SourceKey alone was unique a moment ago.
            migrationBuilder.CreateIndex(
                name: "ux_rag_sources_key_content_format",
                table: "rag_sources",
                columns: new[] { "SourceKey", "ContentHash", "IndexFormatVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rag_domain_sources_domain_revision",
                table: "rag_domain_sources",
                columns: new[] { "DomainKey", "Revision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Downgrading returns to a model that CANNOT represent two content
            // rows for one key. Rather than let PostgreSQL report that as an
            // opaque unique-violation halfway through, the condition is checked
            // first and named.
            if (IsNpgsql(migrationBuilder))
            {
                // Content nothing claims is garbage the newer indexer would have
                // collected anyway; removing it here is not a judgement call and
                // resolves the ordinary case where a domain has already moved on.
                migrationBuilder.Sql(
                    """
                    DELETE FROM rag_sources s
                    WHERE NOT EXISTS (
                        SELECT 1 FROM rag_domain_sources m WHERE m."SourceId" = s."Id");
                    """);

                // What remains is two domains genuinely mid-upgrade, holding two
                // interpretations of one document. The old model has no way to
                // say that, and picking a winner would silently rewrite what one
                // of them is serving. Refused, with the fix stated.
                migrationBuilder.Sql(
                    """
                    DO $$
                    DECLARE conflicting text;
                    BEGIN
                        SELECT string_agg(k, ', ')
                        INTO conflicting
                        FROM (
                            SELECT "SourceKey" AS k
                            FROM rag_sources
                            GROUP BY "SourceKey"
                            HAVING count(*) > 1
                            LIMIT 10) t;

                        IF conflicting IS NOT NULL THEN
                            RAISE EXCEPTION
                                'rag: cannot downgrade while domains hold different content for the same source (%). Reindex every domain at one revision first.',
                                conflicting;
                        END IF;
                    END $$;
                    """);
            }

            migrationBuilder.AddColumn<string>(
                name: "Revision",
                table: "rag_sources",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // The inverse copy, and only where it is unambiguous. A source whose
            // memberships agree on one revision has exactly one honest answer; a
            // source whose memberships disagree has none, and is left empty on
            // purpose — the old retrieval path reads an empty revision as an
            // incoherent index and refuses it until a reindex converges, which is
            // the correct outcome and not a guess.
            if (IsNpgsql(migrationBuilder))
            {
                migrationBuilder.Sql(
                    """
                    UPDATE rag_sources s
                    SET "Revision" = agreed.rev
                    FROM (
                        SELECT "SourceId", min("Revision") AS rev
                        FROM rag_domain_sources
                        GROUP BY "SourceId"
                        HAVING count(DISTINCT "Revision") = 1) agreed
                    WHERE agreed."SourceId" = s."Id";
                    """);
            }

            migrationBuilder.DropIndex(
                name: "ix_rag_sources_key",
                table: "rag_sources");

            migrationBuilder.DropIndex(
                name: "ux_rag_sources_key_content_format",
                table: "rag_sources");

            migrationBuilder.DropIndex(
                name: "ix_rag_domain_sources_domain_revision",
                table: "rag_domain_sources");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "rag_domain_sources");

            migrationBuilder.CreateIndex(
                name: "ix_rag_sources_revision",
                table: "rag_sources",
                column: "Revision");

            migrationBuilder.CreateIndex(
                name: "ux_rag_sources_key",
                table: "rag_sources",
                column: "SourceKey",
                unique: true);
        }

        // The migration set is PostgreSQL-only; the SQLite-backed tests build
        // their schema with EnsureCreated rather than by running migrations.
        // Guarded rather than assumed, so a non-Npgsql provider skips the
        // provider-specific statements instead of failing on them.
        private static bool IsNpgsql(MigrationBuilder migrationBuilder) =>
            migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }
}
