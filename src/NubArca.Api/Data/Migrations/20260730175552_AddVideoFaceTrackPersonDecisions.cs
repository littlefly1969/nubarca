using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoFaceTrackPersonDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_people_id_owner",
                table: "people",
                columns: new[] { "Id", "OwnerUserId" });

            migrationBuilder.CreateTable(
                name: "video_face_track_person_decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoFaceTrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: true),
                    Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_face_track_person_decisions", x => x.Id);
                    table.CheckConstraint("ck_video_face_track_person_decisions_person_matches_decision", "(\"Decision\" = 'assigned' AND \"PersonId\" IS NOT NULL) OR (\"Decision\" = 'ignored' AND \"PersonId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_video_face_track_person_decisions_people_PersonId_OwnerUser~",
                        columns: x => new { x.PersonId, x.OwnerUserId },
                        principalTable: "people",
                        principalColumns: new[] { "Id", "OwnerUserId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_video_face_track_person_decisions_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_video_face_track_person_decisions_video_face_tracks_VideoFa~",
                        column: x => x.VideoFaceTrackId,
                        principalTable: "video_face_tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_video_face_track_person_decisions_owner_decision",
                table: "video_face_track_person_decisions",
                columns: new[] { "OwnerUserId", "Decision" });

            migrationBuilder.CreateIndex(
                name: "ix_video_face_track_person_decisions_owner_person",
                table: "video_face_track_person_decisions",
                columns: new[] { "OwnerUserId", "PersonId" });

            migrationBuilder.CreateIndex(
                name: "IX_video_face_track_person_decisions_PersonId_OwnerUserId",
                table: "video_face_track_person_decisions",
                columns: new[] { "PersonId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "ix_video_face_track_person_decisions_track",
                table: "video_face_track_person_decisions",
                column: "VideoFaceTrackId");

            migrationBuilder.CreateIndex(
                name: "ux_video_face_track_person_decisions_owner_track",
                table: "video_face_track_person_decisions",
                columns: new[] { "OwnerUserId", "VideoFaceTrackId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "video_face_track_person_decisions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_people_id_owner",
                table: "people");
        }
    }
}
