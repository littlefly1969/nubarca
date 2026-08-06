using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlbumTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "album_transfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAlbumId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CoverTransferItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    ItemCount = table.Column<int>(type: "integer", nullable: false),
                    TotalSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAlbumId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_album_transfers", x => x.Id);
                    table.CheckConstraint("ck_album_transfers_created_album", "(\"State\" = 'accepted' AND \"CreatedAlbumId\" IS NOT NULL) OR (\"State\" <> 'accepted' AND \"CreatedAlbumId\" IS NULL)");
                    table.CheckConstraint("ck_album_transfers_recipient_not_sender", "\"RecipientUserId\" <> \"SenderUserId\"");
                    table.CheckConstraint("ck_album_transfers_state", "\"State\" IN ('pending', 'accepted', 'declined', 'cancelled', 'expired', 'failed')");
                    table.ForeignKey(
                        name: "FK_album_transfers_users_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_album_transfers_users_SenderUserId",
                        column: x => x.SenderUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "album_transfer_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AlbumTransferId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    BlobObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceFileItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    EffectiveDateTaken = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_album_transfer_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_album_transfer_items_album_transfers_AlbumTransferId",
                        column: x => x.AlbumTransferId,
                        principalTable: "album_transfers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_album_transfer_items_blob_objects_BlobObjectId",
                        column: x => x.BlobObjectId,
                        principalTable: "blob_objects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_album_transfer_items_blob",
                table: "album_transfer_items",
                column: "BlobObjectId");

            migrationBuilder.CreateIndex(
                name: "ix_album_transfer_items_transfer_order",
                table: "album_transfer_items",
                columns: new[] { "AlbumTransferId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "ix_album_transfers_recipient_state",
                table: "album_transfers",
                columns: new[] { "RecipientUserId", "State" });

            migrationBuilder.CreateIndex(
                name: "ix_album_transfers_sender_state",
                table: "album_transfers",
                columns: new[] { "SenderUserId", "State" });

            migrationBuilder.CreateIndex(
                name: "ix_album_transfers_state_expires",
                table: "album_transfers",
                columns: new[] { "State", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "ux_album_transfers_pending_album_recipient",
                table: "album_transfers",
                columns: new[] { "SourceAlbumId", "RecipientUserId" },
                unique: true,
                filter: "\"State\" = 'pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "album_transfer_items");

            migrationBuilder.DropTable(
                name: "album_transfers");
        }
    }
}
