using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBlobPurgeEligibleAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PurgeEligibleAt",
                table: "blob_objects",
                type: "timestamp with time zone",
                nullable: true);

            // The old schema recorded only CreatedAt, not when the final
            // reference disappeared. Give existing true orphans a full grace
            // window from migration time rather than guessing from their
            // (possibly very old) creation timestamp. A zero-count blob still
            // referenced by a trashed FileItem remains ineligible; its eventual
            // manual purge/sweeper pass will set the timestamp correctly.
            migrationBuilder.Sql(
                """
                UPDATE "blob_objects" AS b
                SET "PurgeEligibleAt" = CURRENT_TIMESTAMP
                WHERE b."ReferenceCount" = 0
                  AND b."PurgeEligibleAt" IS NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "file_items" AS f
                      WHERE f."BlobObjectId" = b."Id"
                  );
                """);

            migrationBuilder.CreateIndex(
                name: "ix_blob_objects_purge_eligible_at",
                table: "blob_objects",
                column: "PurgeEligibleAt",
                filter: "\"ReferenceCount\" = 0 AND \"PurgeEligibleAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_blob_objects_purge_eligible_at",
                table: "blob_objects");

            migrationBuilder.DropColumn(
                name: "PurgeEligibleAt",
                table: "blob_objects");
        }
    }
}
