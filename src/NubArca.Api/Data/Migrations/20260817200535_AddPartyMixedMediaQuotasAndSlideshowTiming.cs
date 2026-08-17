using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyMixedMediaQuotasAndSlideshowTiming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PartyParticipantId",
                table: "party_upload_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxPhotoUploadsPerParticipant",
                table: "party_album_links",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // 60, not 0. EF derives the default from the CLR type, but an
            // existing party link is a REAL row that this column now governs:
            // backfilling 0 would mean "a video may hold the slideshow for zero
            // seconds" on every party that already exists.
            migrationBuilder.AddColumn<int>(
                name: "MaxVideoSlideSeconds",
                table: "party_album_links",
                type: "integer",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<int>(
                name: "MaxVideoUploadsPerParticipant",
                table: "party_album_links",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // 9, matching PartySlideshowDefaults.PhotoSeconds and the historical
            // hardcoded TV interval, so an album that was mid-party when this
            // deployed keeps advancing exactly as it did before.
            migrationBuilder.AddColumn<int>(
                name: "PhotoSlideSeconds",
                table: "party_album_links",
                type: "integer",
                nullable: false,
                defaultValue: 9);

            migrationBuilder.CreateTable(
                name: "party_participants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyAlbumLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AcceptedPhotoCount = table.Column<int>(type: "integer", nullable: false),
                    AcceptedVideoCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_participants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_party_participants_party_album_links_PartyAlbumLinkId",
                        column: x => x.PartyAlbumLinkId,
                        principalTable: "party_album_links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_party_upload_items_PartyParticipantId",
                table: "party_upload_items",
                column: "PartyParticipantId");

            migrationBuilder.CreateIndex(
                name: "ux_party_participants_link_token",
                table: "party_participants",
                columns: new[] { "PartyAlbumLinkId", "TokenHash" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_party_upload_items_party_participants_PartyParticipantId",
                table: "party_upload_items",
                column: "PartyParticipantId",
                principalTable: "party_participants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_party_upload_items_party_participants_PartyParticipantId",
                table: "party_upload_items");

            migrationBuilder.DropTable(
                name: "party_participants");

            migrationBuilder.DropIndex(
                name: "IX_party_upload_items_PartyParticipantId",
                table: "party_upload_items");

            migrationBuilder.DropColumn(
                name: "PartyParticipantId",
                table: "party_upload_items");

            migrationBuilder.DropColumn(
                name: "MaxPhotoUploadsPerParticipant",
                table: "party_album_links");

            migrationBuilder.DropColumn(
                name: "MaxVideoSlideSeconds",
                table: "party_album_links");

            migrationBuilder.DropColumn(
                name: "MaxVideoUploadsPerParticipant",
                table: "party_album_links");

            migrationBuilder.DropColumn(
                name: "PhotoSlideSeconds",
                table: "party_album_links");
        }
    }
}
