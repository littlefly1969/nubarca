using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <summary>
    /// SHARE-ALBUM-03 schema: an explicit album ORDER, a chosen COVER, a stable
    /// surrogate identity for the membership row, and the album's optimistic
    /// concurrency TOKEN.
    ///
    /// Additive in schema; the two backfills are what make it additive in
    /// BEHAVIOUR, and both must run BEFORE the unique constraint and the index:
    ///
    ///  * album_items.Id — the scaffolded default gives every row the SAME
    ///    all-zero GUID, which violates AK_album_items_Id on the second row.
    ///    Filled with real values first.
    ///
    ///  * album_items.SortOrder — numbered in exactly the order the surfaces
    ///    already showed (AddedAt, then FileItemId, the stable tie-break the
    ///    read paths already applied), so no album visibly reshuffles. Without
    ///    it every row would be 0 and each album would fall back to an
    ///    arbitrary order.
    ///
    /// albums.CoverFileItemId stays NULL: "derive the cover from the first
    /// members" remains the behaviour until somebody chooses one.
    ///
    /// PORTABILITY: the SortOrder backfill is a correlated COUNT, valid on
    /// PostgreSQL and SQLite alike. The Id backfill uses gen_random_uuid(),
    /// which is PostgreSQL 13+ only — deliberately, because there is no
    /// portable SQL UUID generator, and the endpoint tests build their schema
    /// with EnsureCreated rather than by running migrations.
    /// </summary>
    public partial class AddAlbumEditorSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CoverFileItemId",
                table: "albums",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "albums",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "Id",
                table: "album_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "album_items",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // (1) Real identities before the unique constraint exists.
            migrationBuilder.Sql(
                """UPDATE album_items SET "Id" = gen_random_uuid();""");

            // (2) Reproduce the previous implicit order exactly. A correlated
            // COUNT rather than ROW_NUMBER() so this is valid on SQLite too and
            // the migration can be rehearsed locally. The <= on the tie-break
            // makes it 1-based and includes the row itself.
            migrationBuilder.Sql(
                """
                UPDATE album_items
                SET "SortOrder" = (
                    SELECT COUNT(*)
                    FROM album_items AS earlier
                    WHERE earlier."AlbumId" = album_items."AlbumId"
                      AND (earlier."AddedAt" < album_items."AddedAt"
                           OR (earlier."AddedAt" = album_items."AddedAt"
                               AND earlier."FileItemId" <= album_items."FileItemId"))
                );
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_album_items_Id",
                table: "album_items",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "ix_album_items_album_sort_order",
                table: "album_items",
                columns: new[] { "AlbumId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropUniqueConstraint(
                name: "AK_album_items_Id",
                table: "album_items");

            migrationBuilder.DropIndex(
                name: "ix_album_items_album_sort_order",
                table: "album_items");

            migrationBuilder.DropColumn(
                name: "CoverFileItemId",
                table: "albums");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "albums");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "album_items");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "album_items");
        }
    }
}
