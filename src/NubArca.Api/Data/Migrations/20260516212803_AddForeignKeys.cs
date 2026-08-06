using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_folders_ParentFolderId",
                table: "folders",
                column: "ParentFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_file_items_ParentFolderId",
                table: "file_items",
                column: "ParentFolderId");

            migrationBuilder.AddForeignKey(
                name: "FK_audit_logs_users_UserId",
                table: "audit_logs",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_file_items_blob_objects_BlobObjectId",
                table: "file_items",
                column: "BlobObjectId",
                principalTable: "blob_objects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_file_items_folders_ParentFolderId",
                table: "file_items",
                column: "ParentFolderId",
                principalTable: "folders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_file_items_users_OwnerUserId",
                table: "file_items",
                column: "OwnerUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_folders_folders_ParentFolderId",
                table: "folders",
                column: "ParentFolderId",
                principalTable: "folders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_folders_users_OwnerUserId",
                table: "folders",
                column: "OwnerUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_audit_logs_users_UserId",
                table: "audit_logs");

            migrationBuilder.DropForeignKey(
                name: "FK_file_items_blob_objects_BlobObjectId",
                table: "file_items");

            migrationBuilder.DropForeignKey(
                name: "FK_file_items_folders_ParentFolderId",
                table: "file_items");

            migrationBuilder.DropForeignKey(
                name: "FK_file_items_users_OwnerUserId",
                table: "file_items");

            migrationBuilder.DropForeignKey(
                name: "FK_folders_folders_ParentFolderId",
                table: "folders");

            migrationBuilder.DropForeignKey(
                name: "FK_folders_users_OwnerUserId",
                table: "folders");

            migrationBuilder.DropIndex(
                name: "IX_folders_ParentFolderId",
                table: "folders");

            migrationBuilder.DropIndex(
                name: "IX_file_items_ParentFolderId",
                table: "file_items");
        }
    }
}
