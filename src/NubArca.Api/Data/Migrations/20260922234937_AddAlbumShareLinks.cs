using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlbumShareLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "album_share_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AlbumId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    UploadEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    AllowOriginalDownload = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    RequireSecondFactor = table.Column<bool>(type: "boolean", nullable: false),
                    Label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    MaxUploads = table.Column<int>(type: "integer", nullable: false, defaultValue: 2000),
                    UploadCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_album_share_links", x => x.Id);
                    table.CheckConstraint("ck_album_share_links_max_uploads", "\"MaxUploads\" >= 0 AND \"MaxUploads\" <= 100000");
                    table.CheckConstraint("ck_album_share_links_upload_count", "\"UploadCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_album_share_links_albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "album_share_guests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AlbumShareLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_album_share_guests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_album_share_guests_album_share_links_AlbumShareLinkId",
                        column: x => x.AlbumShareLinkId,
                        principalTable: "album_share_links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "album_share_challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AlbumShareLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlbumShareGuestId = table.Column<Guid>(type: "uuid", nullable: false),
                    OtpProof = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_album_share_challenges", x => x.Id);
                    table.CheckConstraint("ck_album_share_challenges_attempts", "\"Attempts\" >= 0");
                    table.ForeignKey(
                        name: "FK_album_share_challenges_album_share_guests_AlbumShareGuestId",
                        column: x => x.AlbumShareGuestId,
                        principalTable: "album_share_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_album_share_challenges_album_share_links_AlbumShareLinkId",
                        column: x => x.AlbumShareLinkId,
                        principalTable: "album_share_links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "album_share_devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AlbumShareLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlbumShareGuestId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_album_share_devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_album_share_devices_album_share_guests_AlbumShareGuestId",
                        column: x => x.AlbumShareGuestId,
                        principalTable: "album_share_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_album_share_devices_album_share_links_AlbumShareLinkId",
                        column: x => x.AlbumShareLinkId,
                        principalTable: "album_share_links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_album_share_challenges_AlbumShareLinkId",
                table: "album_share_challenges",
                column: "AlbumShareLinkId");

            migrationBuilder.CreateIndex(
                name: "ux_album_share_challenges_guest",
                table: "album_share_challenges",
                column: "AlbumShareGuestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_album_share_devices_AlbumShareGuestId",
                table: "album_share_devices",
                column: "AlbumShareGuestId");

            migrationBuilder.CreateIndex(
                name: "IX_album_share_devices_AlbumShareLinkId",
                table: "album_share_devices",
                column: "AlbumShareLinkId");

            migrationBuilder.CreateIndex(
                name: "ux_album_share_devices_token",
                table: "album_share_devices",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_album_share_guests_email",
                table: "album_share_guests",
                columns: new[] { "AlbumShareLinkId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_album_share_links_album",
                table: "album_share_links",
                columns: new[] { "AlbumId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "ux_album_share_links_one_live",
                table: "album_share_links",
                column: "AlbumId",
                unique: true,
                filter: "\"Enabled\" AND \"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_album_share_links_token",
                table: "album_share_links",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "album_share_challenges");

            migrationBuilder.DropTable(
                name: "album_share_devices");

            migrationBuilder.DropTable(
                name: "album_share_guests");

            migrationBuilder.DropTable(
                name: "album_share_links");
        }
    }
}
