using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyFaceSearchTvActivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FaceCropBlobObjectId",
                table: "party_face_search_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TvActivatedAt",
                table: "party_face_search_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TvActivationVersion",
                table: "party_face_search_sessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_party_face_search_sessions_FaceCropBlobObjectId",
                table: "party_face_search_sessions",
                column: "FaceCropBlobObjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_party_face_search_sessions_blob_objects_FaceCropBlobObjectId",
                table: "party_face_search_sessions",
                column: "FaceCropBlobObjectId",
                principalTable: "blob_objects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_party_face_search_sessions_blob_objects_FaceCropBlobObjectId",
                table: "party_face_search_sessions");

            migrationBuilder.DropIndex(
                name: "IX_party_face_search_sessions_FaceCropBlobObjectId",
                table: "party_face_search_sessions");

            migrationBuilder.DropColumn(
                name: "FaceCropBlobObjectId",
                table: "party_face_search_sessions");

            migrationBuilder.DropColumn(
                name: "TvActivatedAt",
                table: "party_face_search_sessions");

            migrationBuilder.DropColumn(
                name: "TvActivationVersion",
                table: "party_face_search_sessions");
        }
    }
}
