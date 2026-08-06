using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlbumMemberships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "album_memberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AlbumId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AllowOriginalDownload = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    InvitedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvitedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeclinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_album_memberships", x => x.Id);
                    table.CheckConstraint("ck_album_memberships_role", "\"Role\" IN ('viewer', 'contributor', 'editor')");
                    table.CheckConstraint("ck_album_memberships_state", "\"State\" IN ('pending', 'accepted', 'declined', 'revoked')");
                    table.ForeignKey(
                        name: "FK_album_memberships_albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_album_memberships_users_InvitedByUserId",
                        column: x => x.InvitedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_album_memberships_users_MemberUserId",
                        column: x => x.MemberUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_album_memberships_album_state",
                table: "album_memberships",
                columns: new[] { "AlbumId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_album_memberships_InvitedByUserId",
                table: "album_memberships",
                column: "InvitedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_album_memberships_member_state",
                table: "album_memberships",
                columns: new[] { "MemberUserId", "State" });

            migrationBuilder.CreateIndex(
                name: "ux_album_memberships_album_member",
                table: "album_memberships",
                columns: new[] { "AlbumId", "MemberUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "album_memberships");
        }
    }
}
