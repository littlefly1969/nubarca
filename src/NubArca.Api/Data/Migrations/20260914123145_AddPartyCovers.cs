using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyCovers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "InvitationCoverFileItemId",
                table: "parties",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LiveCoverFileItemId",
                table: "parties",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_parties_invitation_cover_file",
                table: "parties",
                column: "InvitationCoverFileItemId");

            migrationBuilder.CreateIndex(
                name: "ix_parties_live_cover_file",
                table: "parties",
                column: "LiveCoverFileItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_parties_file_items_InvitationCoverFileItemId",
                table: "parties",
                column: "InvitationCoverFileItemId",
                principalTable: "file_items",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_parties_file_items_LiveCoverFileItemId",
                table: "parties",
                column: "LiveCoverFileItemId",
                principalTable: "file_items",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_parties_file_items_InvitationCoverFileItemId",
                table: "parties");

            migrationBuilder.DropForeignKey(
                name: "FK_parties_file_items_LiveCoverFileItemId",
                table: "parties");

            migrationBuilder.DropIndex(
                name: "ix_parties_invitation_cover_file",
                table: "parties");

            migrationBuilder.DropIndex(
                name: "ix_parties_live_cover_file",
                table: "parties");

            migrationBuilder.DropColumn(
                name: "InvitationCoverFileItemId",
                table: "parties");

            migrationBuilder.DropColumn(
                name: "LiveCoverFileItemId",
                table: "parties");
        }
    }
}
