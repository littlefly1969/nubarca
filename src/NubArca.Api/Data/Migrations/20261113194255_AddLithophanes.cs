using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLithophanes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lithophane_projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SettingsJson = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    EngineVersion = table.Column<int>(type: "integer", nullable: false),
                    PixelMapStorageKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ArtifactStorageKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lithophane_projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lithophane_projects_file_items_FileItemId",
                        column: x => x.FileItemId,
                        principalTable: "file_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_lithophane_projects_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lithophane_strips",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstRow = table.Column<int>(type: "integer", nullable: false),
                    EndRow = table.Column<int>(type: "integer", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lithophane_strips", x => new { x.ProjectId, x.FirstRow });
                    table.ForeignKey(
                        name: "FK_lithophane_strips_lithophane_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "lithophane_projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lithophane_projects_ArtifactStorageKey",
                table: "lithophane_projects",
                column: "ArtifactStorageKey");

            migrationBuilder.CreateIndex(
                name: "IX_lithophane_projects_FileItemId",
                table: "lithophane_projects",
                column: "FileItemId");

            migrationBuilder.CreateIndex(
                name: "IX_lithophane_projects_OwnerUserId_CreatedAt_Id",
                table: "lithophane_projects",
                columns: new[] { "OwnerUserId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_lithophane_projects_OwnerUserId_RequestId",
                table: "lithophane_projects",
                columns: new[] { "OwnerUserId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lithophane_projects_PixelMapStorageKey",
                table: "lithophane_projects",
                column: "PixelMapStorageKey");

            migrationBuilder.CreateIndex(
                name: "IX_lithophane_strips_StorageKey",
                table: "lithophane_strips",
                column: "StorageKey");

            // Existing Member roles are operator-owned and never re-seeded.
            // Grant the new non-administrative feature once, leaving custom
            // roles and Restricted to the operator's explicit decisions.
            migrationBuilder.Sql("""
                INSERT INTO role_permissions ("RoleKey", "PermissionKey")
                SELECT 'Member', 'laboratory.lithophanes'
                WHERE EXISTS (SELECT 1 FROM access_roles WHERE "Key" = 'Member')
                  AND NOT EXISTS (SELECT 1 FROM role_permissions
                      WHERE "RoleKey" = 'Member' AND "PermissionKey" = 'laboratory.lithophanes');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lithophane_strips");

            migrationBuilder.DropTable(
                name: "lithophane_projects");
            // Keep the permission row: it may have existed before Up or been
            // explicitly granted by an operator. Older catalogues ignore it.
        }
    }
}
