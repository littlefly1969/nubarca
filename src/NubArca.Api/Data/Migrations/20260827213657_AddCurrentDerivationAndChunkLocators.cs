using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// Rich ingestion needs two things the Slice-3 schema cannot say: WHICH
    /// extraction of a file is the current one, and WHERE in its own document a
    /// chunk sits.
    ///
    /// The dangerous half is the first. `IsCurrent` arrives defaulting to false,
    /// and the retrieval boundary requires it — so an upgrade that adds the
    /// column and stops has just made every private corpus in the installation
    /// unreachable, silently, with no error anywhere. Every existing row has to
    /// be claimed by this migration, and this is the only moment that claim can
    /// be made honestly: after it, nothing knows which reading of a file was the
    /// one being served.
    public partial class AddCurrentDerivationAndChunkLocators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCurrent",
                table: "document_texts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            if (IsNpgsql(migrationBuilder))
            {
                // CLAIM THE UNAMBIGUOUS ROWS, AND ONLY THOSE.
                //
                // Slice 3 shipped exactly one production extraction profile, so
                // a database it wrote has one row per file and every row is the
                // current reading of its file by construction. That is the case
                // this statement is for, and it covers ordinary production.
                //
                // A file carrying more than one row is a state Slice 3 could
                // only reach if its extraction profile was recreated under a new
                // id. There is no honest way to pick a winner: the rows differ
                // by a profile whose meaning this migration cannot read, and
                // choosing the newest, the first, or the one with the most text
                // would be inventing provenance and then serving somebody's
                // document through it. Those files are left with no current row,
                // which makes them not retrievable until `documents index` runs
                // and establishes one — the reindex is cheap, and being briefly
                // without an answer is recoverable in a way that quietly
                // answering from the wrong reading is not.
                //
                // Status is deliberately not filtered. A skipped or failed row
                // IS the current interpretation of its file; the retrieval
                // boundary requires completion separately, so marking it here
                // keeps "one current row per file" a true statement rather than
                // one that holds only for successes.
                migrationBuilder.Sql(
                    """
                    UPDATE document_texts d
                    SET "IsCurrent" = true
                    WHERE NOT EXISTS (
                        SELECT 1 FROM document_texts o
                        WHERE o."FileItemId" = d."FileItemId"
                          AND o."Id" <> d."Id");
                    """);
            }

            migrationBuilder.AddColumn<int>(
                name: "LocatorIndex",
                table: "document_chunks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocatorKind",
                table: "document_chunks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocatorLabel",
                table: "document_chunks",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            // Created AFTER the backfill on purpose. If the statement above ever
            // produced two current rows for one file, this index refuses to
            // build and the migration fails — a deploy that stops is the correct
            // outcome, and far better than an installation that starts serving
            // two readings of the same document.
            migrationBuilder.CreateIndex(
                name: "ux_document_texts_current_per_file",
                table: "document_texts",
                column: "FileItemId",
                unique: true,
                filter: "\"IsCurrent\"");

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_chunks_locator_index_positive",
                table: "document_chunks",
                sql: "\"LocatorIndex\" IS NULL OR \"LocatorIndex\" >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_chunks_page_positive",
                table: "document_chunks",
                sql: "\"Page\" IS NULL OR \"Page\" >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_document_texts_current_per_file",
                table: "document_texts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_chunks_locator_index_positive",
                table: "document_chunks");

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_chunks_page_positive",
                table: "document_chunks");

            migrationBuilder.DropColumn(
                name: "IsCurrent",
                table: "document_texts");

            migrationBuilder.DropColumn(
                name: "LocatorIndex",
                table: "document_chunks");

            migrationBuilder.DropColumn(
                name: "LocatorKind",
                table: "document_chunks");

            migrationBuilder.DropColumn(
                name: "LocatorLabel",
                table: "document_chunks");
        }

        // Down needs no data step. Dropping `IsCurrent` restores exactly the
        // Slice-3 meaning — one row per (file, profile), all of them authority —
        // and the flag it discards carried nothing that cannot be recomputed by
        // reindexing. The locator columns are pure additions; the provenance
        // they held is re-derived by the extractor that wrote it.
        private static bool IsNpgsql(MigrationBuilder migrationBuilder) =>
            migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }
}
