using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonFaceReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "person_face_references",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    FaceDetectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_person_face_references", x => x.Id);
                    table.CheckConstraint("ck_person_face_references_ordinal_range", "\"Ordinal\" >= 0 AND \"Ordinal\" < 6");
                    table.ForeignKey(
                        name: "FK_person_face_references_ai_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_person_face_references_face_detections_FaceDetectionId",
                        column: x => x.FaceDetectionId,
                        principalTable: "face_detections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_person_face_references_people_PersonId",
                        column: x => x.PersonId,
                        principalTable: "people",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_person_face_references_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_person_face_references_FaceDetectionId",
                table: "person_face_references",
                column: "FaceDetectionId");

            migrationBuilder.CreateIndex(
                name: "IX_person_face_references_PersonId",
                table: "person_face_references",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_person_face_references_ProfileId",
                table: "person_face_references",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "ux_person_face_references_owner_person_profile_face",
                table: "person_face_references",
                columns: new[] { "OwnerUserId", "PersonId", "ProfileId", "FaceDetectionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_person_face_references_owner_person_profile_ordinal",
                table: "person_face_references",
                columns: new[] { "OwnerUserId", "PersonId", "ProfileId", "Ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "person_face_references");
        }
    }
}
