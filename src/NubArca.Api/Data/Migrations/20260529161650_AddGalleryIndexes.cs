using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGalleryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_file_items_owner_deleted_created_id",
                table: "file_items",
                columns: new[] { "OwnerUserId", "DeletedAt", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "ix_file_items_owner_deleted_size_id",
                table: "file_items",
                columns: new[] { "OwnerUserId", "DeletedAt", "SizeBytes", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_file_items_owner_deleted_created_id",
                table: "file_items");

            migrationBuilder.DropIndex(
                name: "ix_file_items_owner_deleted_size_id",
                table: "file_items");
        }
    }
}
