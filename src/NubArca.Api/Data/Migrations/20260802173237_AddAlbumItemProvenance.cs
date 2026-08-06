using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <summary>
    /// SHARE-ALBUM-02: gives album_items provenance — who put the item there.
    ///
    /// Additive, but NOT a bare AddColumn: the new column is non-nullable and
    /// carries a foreign key to users, so the scaffolded all-zeroes default
    /// would violate that key on the first existing row. The backfill therefore
    /// runs BETWEEN adding the column and adding the constraint.
    ///
    /// Backfilling to the album's owner is accurate rather than a placeholder:
    /// before this slice nobody but the owner could add an item to an album, so
    /// every pre-existing row genuinely was added by them. That is what makes a
    /// non-nullable column the right shape — no "null means the owner" special
    /// case has to be remembered in each of the query predicates that now decide
    /// whether a contributed item is servable.
    /// </summary>
    public partial class AddAlbumItemProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AddedByUserId",
                table: "album_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Correlated subquery rather than `UPDATE ... FROM`: this shape is
            // valid on PostgreSQL and SQLite alike, so the migration is not the
            // one statement in the chain that cannot be rehearsed locally.
            // album_items.AlbumId has an FK Restrict to albums, so the subquery
            // cannot return NULL for any existing row.
            migrationBuilder.Sql(
                """
                UPDATE album_items
                SET "AddedByUserId" = (
                    SELECT a."OwnerUserId" FROM albums a WHERE a."Id" = album_items."AlbumId"
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_album_items_AddedByUserId",
                table: "album_items",
                column: "AddedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_album_items_album_added_by",
                table: "album_items",
                columns: new[] { "AlbumId", "AddedByUserId" });

            migrationBuilder.AddForeignKey(
                name: "FK_album_items_users_AddedByUserId",
                table: "album_items",
                column: "AddedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_album_items_users_AddedByUserId",
                table: "album_items");

            migrationBuilder.DropIndex(
                name: "IX_album_items_AddedByUserId",
                table: "album_items");

            migrationBuilder.DropIndex(
                name: "ix_album_items_album_added_by",
                table: "album_items");

            migrationBuilder.DropColumn(
                name: "AddedByUserId",
                table: "album_items");
        }
    }
}
