using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNumericInvariantChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_share_links_download_count_non_negative",
                table: "share_links",
                sql: "\"DownloadCount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_share_links_max_downloads_positive_or_null",
                table: "share_links",
                sql: "\"MaxDownloads\" IS NULL OR \"MaxDownloads\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_file_items_size_bytes_non_negative",
                table: "file_items",
                sql: "\"SizeBytes\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_blob_objects_reference_count_non_negative",
                table: "blob_objects",
                sql: "\"ReferenceCount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_blob_objects_size_bytes_non_negative",
                table: "blob_objects",
                sql: "\"SizeBytes\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_share_links_download_count_non_negative",
                table: "share_links");

            migrationBuilder.DropCheckConstraint(
                name: "ck_share_links_max_downloads_positive_or_null",
                table: "share_links");

            migrationBuilder.DropCheckConstraint(
                name: "ck_file_items_size_bytes_non_negative",
                table: "file_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_blob_objects_reference_count_non_negative",
                table: "blob_objects");

            migrationBuilder.DropCheckConstraint(
                name: "ck_blob_objects_size_bytes_non_negative",
                table: "blob_objects");
        }
    }
}
