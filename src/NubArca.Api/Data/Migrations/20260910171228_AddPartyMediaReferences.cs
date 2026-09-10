using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <summary>
    /// Party MEDIA REFERENCES: a guest-content slot may carry one photograph,
    /// and an activity's picture becomes a real relation — both pointing at the
    /// owner's ordinary <c>file_items</c> row, independently of any album.
    ///
    /// <para>One nullable column on <c>party_guest_contents</c>, two lookup
    /// indexes, and two foreign keys with <c>ON DELETE SET NULL</c>: a permanent
    /// purge of the file nulls the reference instead of being blocked by it, and
    /// the slot or activity survives without a picture. Nothing is backfilled and
    /// no existing value is rewritten.</para>
    ///
    /// <para><c>party_challenges.MediaFileItemId</c> is older than this
    /// migration and was never constrained, and the previous application did not
    /// clear it when a file was purged. Adding the constraint validates every
    /// existing row, so the references are checked FIRST and a dangling one is
    /// refused loudly rather than repaired: nulling a host's activity picture is
    /// an operator's decision, not a migration's. The migration runs in one
    /// transaction, so a refusal leaves the schema exactly as it was.</para>
    /// </summary>
    public partial class AddPartyMediaReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM party_challenges c
                        WHERE c."MediaFileItemId" IS NOT NULL
                          AND NOT EXISTS (
                              SELECT 1 FROM file_items f WHERE f."Id" = c."MediaFileItemId"))
                    THEN
                        RAISE EXCEPTION 'party_challenges.MediaFileItemId references a file_items row that no longer exists; refusing to add FK_party_challenges_file_items_MediaFileItemId without an operator decision';
                    END IF;
                END $$;
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "MediaFileItemId",
                table: "party_guest_contents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_party_guest_contents_media_file",
                table: "party_guest_contents",
                column: "MediaFileItemId");

            migrationBuilder.CreateIndex(
                name: "ix_party_challenges_media_file",
                table: "party_challenges",
                column: "MediaFileItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_party_challenges_file_items_MediaFileItemId",
                table: "party_challenges",
                column: "MediaFileItemId",
                principalTable: "file_items",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_party_guest_contents_file_items_MediaFileItemId",
                table: "party_guest_contents",
                column: "MediaFileItemId",
                principalTable: "file_items",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_party_challenges_file_items_MediaFileItemId",
                table: "party_challenges");

            migrationBuilder.DropForeignKey(
                name: "FK_party_guest_contents_file_items_MediaFileItemId",
                table: "party_guest_contents");

            migrationBuilder.DropIndex(
                name: "ix_party_guest_contents_media_file",
                table: "party_guest_contents");

            migrationBuilder.DropIndex(
                name: "ix_party_challenges_media_file",
                table: "party_challenges");

            migrationBuilder.DropColumn(
                name: "MediaFileItemId",
                table: "party_guest_contents");
        }
    }
}
