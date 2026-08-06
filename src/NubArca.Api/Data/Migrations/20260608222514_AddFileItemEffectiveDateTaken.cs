using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFileItemEffectiveDateTaken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive denormalization of the effective capture date onto
            // file_items. Add the columns nullable first, backfill every existing
            // row from the layered sources (user override → embedded blob date →
            // CreatedAt), then enforce NOT NULL and build the gallery sort index.
            migrationBuilder.AddColumn<DateTime>(
                name: "EffectiveDateTaken",
                table: "file_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EffectiveDateTakenSource",
                table: "file_items",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            // Backfill. Scalar subqueries with LIMIT 1 mirror the EF
            // FirstOrDefault() precedence and are safe regardless of how many
            // metadata rows exist per file/blob. The sources of truth
            // (file_item_user_metadata.DateTakenOverride, blob_metadata.DateTaken)
            // are left untouched — this only populates the denormalized column.
            migrationBuilder.Sql(@"
                UPDATE file_items f SET
                    ""EffectiveDateTaken"" = COALESCE(
                        (SELECT u.""DateTakenOverride"" FROM file_item_user_metadata u
                            WHERE u.""FileItemId"" = f.""Id"" LIMIT 1),
                        (SELECT m.""DateTaken"" FROM blob_metadata m
                            WHERE m.""BlobObjectId"" = f.""BlobObjectId"" LIMIT 1),
                        f.""CreatedAt""),
                    ""EffectiveDateTakenSource"" = CASE
                        WHEN (SELECT u.""DateTakenOverride"" FROM file_item_user_metadata u
                                WHERE u.""FileItemId"" = f.""Id"" LIMIT 1) IS NOT NULL THEN 'user'
                        WHEN (SELECT m.""DateTaken"" FROM blob_metadata m
                                WHERE m.""BlobObjectId"" = f.""BlobObjectId"" LIMIT 1) IS NOT NULL THEN 'embedded'
                        ELSE 'uploaded'
                    END;");

            migrationBuilder.Sql(
                @"ALTER TABLE file_items ALTER COLUMN ""EffectiveDateTaken"" SET NOT NULL;");

            // Partial index over active rows only (the gallery always filters
            // DeletedAt IS NULL). Keeping DeletedAt in the index PREDICATE rather
            // than the key lets PostgreSQL provide the (EffectiveDateTaken, Id)
            // ordering directly from an ordered index scan.
            migrationBuilder.CreateIndex(
                name: "ix_file_items_owner_deleted_effdate_id",
                table: "file_items",
                columns: new[] { "OwnerUserId", "EffectiveDateTaken", "Id" },
                filter: "\"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_file_items_owner_deleted_effdate_id",
                table: "file_items");

            migrationBuilder.DropColumn(
                name: "EffectiveDateTaken",
                table: "file_items");

            migrationBuilder.DropColumn(
                name: "EffectiveDateTakenSource",
                table: "file_items");
        }
    }
}
