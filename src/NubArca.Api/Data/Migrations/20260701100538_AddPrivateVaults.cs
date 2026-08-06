using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivateVaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_folders_active_sibling_name",
                table: "folders");

            migrationBuilder.DropIndex(
                name: "ux_file_items_active_sibling_name",
                table: "file_items");

            migrationBuilder.AddColumn<Guid>(
                name: "PrivateVaultId",
                table: "folders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PrivateVaultId",
                table: "file_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "private_vaults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    EncryptionMode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EncryptionMetadataJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_private_vaults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_private_vaults_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "private_vault_access_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrivateVaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_private_vault_access_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_private_vault_access_tokens_private_vaults_PrivateVaultId",
                        column: x => x.PrivateVaultId,
                        principalTable: "private_vaults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_private_vault_access_tokens_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_folders_owner_vault_parent",
                table: "folders",
                columns: new[] { "OwnerUserId", "PrivateVaultId", "ParentFolderId" },
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_folders_PrivateVaultId",
                table: "folders",
                column: "PrivateVaultId");

            migrationBuilder.CreateIndex(
                name: "ux_folders_active_sibling_name",
                table: "folders",
                columns: new[] { "OwnerUserId", "ParentFolderId", "Name" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL AND \"PrivateVaultId\" IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ux_folders_active_vault_sibling_name",
                table: "folders",
                columns: new[] { "OwnerUserId", "PrivateVaultId", "ParentFolderId", "Name" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL AND \"PrivateVaultId\" IS NOT NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_file_items_owner_vault_parent",
                table: "file_items",
                columns: new[] { "OwnerUserId", "PrivateVaultId", "ParentFolderId" },
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_file_items_PrivateVaultId",
                table: "file_items",
                column: "PrivateVaultId");

            migrationBuilder.CreateIndex(
                name: "ux_file_items_active_sibling_name",
                table: "file_items",
                columns: new[] { "OwnerUserId", "ParentFolderId", "Name" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL AND \"PrivateVaultId\" IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ux_file_items_active_vault_sibling_name",
                table: "file_items",
                columns: new[] { "OwnerUserId", "PrivateVaultId", "ParentFolderId", "Name" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL AND \"PrivateVaultId\" IS NOT NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_private_vault_access_tokens_owner_expires",
                table: "private_vault_access_tokens",
                columns: new[] { "OwnerUserId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_private_vault_access_tokens_PrivateVaultId",
                table: "private_vault_access_tokens",
                column: "PrivateVaultId");

            migrationBuilder.CreateIndex(
                name: "ux_private_vault_access_tokens_token_hash",
                table: "private_vault_access_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_private_vaults_owner",
                table: "private_vaults",
                column: "OwnerUserId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_file_items_private_vaults_PrivateVaultId",
                table: "file_items",
                column: "PrivateVaultId",
                principalTable: "private_vaults",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_folders_private_vaults_PrivateVaultId",
                table: "folders",
                column: "PrivateVaultId",
                principalTable: "private_vaults",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_file_items_private_vaults_PrivateVaultId",
                table: "file_items");

            migrationBuilder.DropForeignKey(
                name: "FK_folders_private_vaults_PrivateVaultId",
                table: "folders");

            migrationBuilder.DropTable(
                name: "private_vault_access_tokens");

            migrationBuilder.DropTable(
                name: "private_vaults");

            migrationBuilder.DropIndex(
                name: "ix_folders_owner_vault_parent",
                table: "folders");

            migrationBuilder.DropIndex(
                name: "IX_folders_PrivateVaultId",
                table: "folders");

            migrationBuilder.DropIndex(
                name: "ux_folders_active_sibling_name",
                table: "folders");

            migrationBuilder.DropIndex(
                name: "ux_folders_active_vault_sibling_name",
                table: "folders");

            migrationBuilder.DropIndex(
                name: "ix_file_items_owner_vault_parent",
                table: "file_items");

            migrationBuilder.DropIndex(
                name: "IX_file_items_PrivateVaultId",
                table: "file_items");

            migrationBuilder.DropIndex(
                name: "ux_file_items_active_sibling_name",
                table: "file_items");

            migrationBuilder.DropIndex(
                name: "ux_file_items_active_vault_sibling_name",
                table: "file_items");

            migrationBuilder.DropColumn(
                name: "PrivateVaultId",
                table: "folders");

            migrationBuilder.DropColumn(
                name: "PrivateVaultId",
                table: "file_items");

            migrationBuilder.CreateIndex(
                name: "ux_folders_active_sibling_name",
                table: "folders",
                columns: new[] { "OwnerUserId", "ParentFolderId", "Name" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ux_file_items_active_sibling_name",
                table: "file_items",
                columns: new[] { "OwnerUserId", "ParentFolderId", "Name" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
