using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTvDisplayAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssignedPartyAlbumLinkId",
                table: "tv_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayAssignment",
                table: "tv_sessions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "general");

            migrationBuilder.CreateIndex(
                name: "ix_tv_sessions_assigned_party_link",
                table: "tv_sessions",
                column: "AssignedPartyAlbumLinkId");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tv_sessions_display_assignment",
                table: "tv_sessions",
                sql: "(\"DisplayAssignment\" = 'general' AND \"AssignedPartyAlbumLinkId\" IS NULL) OR (\"DisplayAssignment\" = 'party' AND \"AssignedPartyAlbumLinkId\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_tv_sessions_party_album_links_AssignedPartyAlbumLinkId",
                table: "tv_sessions",
                column: "AssignedPartyAlbumLinkId",
                principalTable: "party_album_links",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tv_sessions_party_album_links_AssignedPartyAlbumLinkId",
                table: "tv_sessions");

            migrationBuilder.DropIndex(
                name: "ix_tv_sessions_assigned_party_link",
                table: "tv_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tv_sessions_display_assignment",
                table: "tv_sessions");

            migrationBuilder.DropColumn(
                name: "AssignedPartyAlbumLinkId",
                table: "tv_sessions");

            migrationBuilder.DropColumn(
                name: "DisplayAssignment",
                table: "tv_sessions");
        }
    }
}
