using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <summary>
    /// The guest book becomes a book of PHOTOGRAPH MEMORIES: every entry from
    /// here on carries its own reference to a photograph's blob, its framing,
    /// its template and version, and a required signature.
    ///
    /// <para><b>Destructive, deliberately.</b> The text-only book it replaces
    /// was a prototype whose rows are expendable, and a text-only row cannot
    /// satisfy the new invariants — it has no photograph to own. So the rows go
    /// FIRST, before any NOT NULL column or constraint arrives, and no column
    /// gains a placeholder default (a zero blob id, version 0) that exists only
    /// to let an old row survive: from this migration on, an entry without a
    /// photograph cannot be written.</para>
    ///
    /// <para>The quota is PERSISTED apart from the entries
    /// (<c>party_participants.SubmittedGuestbookCount</c>), so it is reset with
    /// them — otherwise a guest who wrote a prototype dedication would find
    /// their allowance spent on something that no longer exists.</para>
    ///
    /// <para>No row deleted here held a blob reference (the prototype had no
    /// column for one), so nothing needs releasing on the way up.</para>
    /// </summary>
    public partial class ReshapeGuestbookAsPhotoMemories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM party_guestbook_entries;");
            migrationBuilder.Sql(
                "UPDATE party_participants SET \"SubmittedGuestbookCount\" = 0 "
                + "WHERE \"SubmittedGuestbookCount\" <> 0;");

            migrationBuilder.AlterColumn<string>(
                name: "AuthorDisplayName",
                table: "party_guestbook_entries",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(160)",
                oldMaxLength: 160,
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BlobObjectId",
                table: "party_guestbook_entries",
                type: "uuid",
                nullable: false);

            migrationBuilder.AddColumn<double>(
                name: "CropCenterX",
                table: "party_guestbook_entries",
                type: "double precision",
                nullable: false);

            migrationBuilder.AddColumn<double>(
                name: "CropCenterY",
                table: "party_guestbook_entries",
                type: "double precision",
                nullable: false);

            migrationBuilder.AddColumn<double>(
                name: "CropZoom",
                table: "party_guestbook_entries",
                type: "double precision",
                nullable: false);

            migrationBuilder.AddColumn<int>(
                name: "PhotoHeight",
                table: "party_guestbook_entries",
                type: "integer",
                nullable: false);

            migrationBuilder.AddColumn<int>(
                name: "PhotoWidth",
                table: "party_guestbook_entries",
                type: "integer",
                nullable: false);

            migrationBuilder.AddColumn<Guid>(
                name: "PreviewBlobObjectId",
                table: "party_guestbook_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemplateKey",
                table: "party_guestbook_entries",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false);

            migrationBuilder.AddColumn<int>(
                name: "TemplateVersion",
                table: "party_guestbook_entries",
                type: "integer",
                nullable: false);

            migrationBuilder.CreateIndex(
                name: "IX_party_guestbook_entries_BlobObjectId",
                table: "party_guestbook_entries",
                column: "BlobObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_party_guestbook_entries_PreviewBlobObjectId",
                table: "party_guestbook_entries",
                column: "PreviewBlobObjectId");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_guestbook_entries_crop",
                table: "party_guestbook_entries",
                sql: "\"CropZoom\" BETWEEN 1 AND 4 AND \"CropCenterX\" BETWEEN 0 AND 1 AND \"CropCenterY\" BETWEEN 0 AND 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_guestbook_entries_photo_size",
                table: "party_guestbook_entries",
                sql: "\"PhotoWidth\" > 0 AND \"PhotoHeight\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_party_guestbook_entries_template_version",
                table: "party_guestbook_entries",
                sql: "\"TemplateVersion\" >= 1");

            migrationBuilder.AddForeignKey(
                name: "FK_party_guestbook_entries_blob_objects_BlobObjectId",
                table: "party_guestbook_entries",
                column: "BlobObjectId",
                principalTable: "blob_objects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_party_guestbook_entries_blob_objects_PreviewBlobObjectId",
                table: "party_guestbook_entries",
                column: "PreviewBlobObjectId",
                principalTable: "blob_objects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The memories cannot be carried back: the previous schema has no
            // column for a photograph. They are deleted, and the references
            // they held — the photograph's, and the preview's once drawn — are
            // released first, under the same rule IBlobService.ReleaseAsync
            // applies (never below zero; reaching zero starts the janitor's
            // grace window), so no blob is left pinned by a row that is gone.
            migrationBuilder.Sql(
                """
                UPDATE blob_objects AS b
                SET "ReferenceCount" = GREATEST(b."ReferenceCount" - r.n, 0),
                    "PurgeEligibleAt" = CASE
                        WHEN b."ReferenceCount" - r.n <= 0 THEN now()
                        ELSE b."PurgeEligibleAt"
                    END
                FROM (
                    SELECT held, count(*) AS n FROM (
                        SELECT "BlobObjectId" AS held FROM party_guestbook_entries
                        UNION ALL
                        SELECT "PreviewBlobObjectId" FROM party_guestbook_entries
                        WHERE "PreviewBlobObjectId" IS NOT NULL
                    ) AS refs
                    GROUP BY held
                ) AS r
                WHERE b."Id" = r.held;
                """);
            migrationBuilder.Sql("DELETE FROM party_guestbook_entries;");
            migrationBuilder.Sql(
                "UPDATE party_participants SET \"SubmittedGuestbookCount\" = 0 "
                + "WHERE \"SubmittedGuestbookCount\" <> 0;");

            migrationBuilder.DropForeignKey(
                name: "FK_party_guestbook_entries_blob_objects_BlobObjectId",
                table: "party_guestbook_entries");

            migrationBuilder.DropForeignKey(
                name: "FK_party_guestbook_entries_blob_objects_PreviewBlobObjectId",
                table: "party_guestbook_entries");

            migrationBuilder.DropIndex(
                name: "IX_party_guestbook_entries_BlobObjectId",
                table: "party_guestbook_entries");

            migrationBuilder.DropIndex(
                name: "IX_party_guestbook_entries_PreviewBlobObjectId",
                table: "party_guestbook_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_party_guestbook_entries_crop",
                table: "party_guestbook_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_party_guestbook_entries_photo_size",
                table: "party_guestbook_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_party_guestbook_entries_template_version",
                table: "party_guestbook_entries");

            migrationBuilder.DropColumn(
                name: "BlobObjectId",
                table: "party_guestbook_entries");

            migrationBuilder.DropColumn(
                name: "CropCenterX",
                table: "party_guestbook_entries");

            migrationBuilder.DropColumn(
                name: "CropCenterY",
                table: "party_guestbook_entries");

            migrationBuilder.DropColumn(
                name: "CropZoom",
                table: "party_guestbook_entries");

            migrationBuilder.DropColumn(
                name: "PhotoHeight",
                table: "party_guestbook_entries");

            migrationBuilder.DropColumn(
                name: "PhotoWidth",
                table: "party_guestbook_entries");

            migrationBuilder.DropColumn(
                name: "PreviewBlobObjectId",
                table: "party_guestbook_entries");

            migrationBuilder.DropColumn(
                name: "TemplateKey",
                table: "party_guestbook_entries");

            migrationBuilder.DropColumn(
                name: "TemplateVersion",
                table: "party_guestbook_entries");

            migrationBuilder.AlterColumn<string>(
                name: "AuthorDisplayName",
                table: "party_guestbook_entries",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(160)",
                oldMaxLength: 160);
        }
    }
}
