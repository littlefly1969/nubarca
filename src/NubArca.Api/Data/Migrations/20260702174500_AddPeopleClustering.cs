using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPeopleClustering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "people",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_people", x => x.Id);
                    table.ForeignKey(
                        name: "FK_people_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "face_clusters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepresentativeFaceDetectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConfidenceAggregate = table.Column<double>(type: "double precision", nullable: true),
                    MemberCount = table.Column<int>(type: "integer", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClusterKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_face_clusters", x => x.Id);
                    table.CheckConstraint("ck_face_clusters_member_count_non_negative", "\"MemberCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_face_clusters_ai_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_face_clusters_people_PersonId",
                        column: x => x.PersonId,
                        principalTable: "people",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_face_clusters_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "person_face_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    FaceDetectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_person_face_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_person_face_assignments_face_detections_FaceDetectionId",
                        column: x => x.FaceDetectionId,
                        principalTable: "face_detections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_person_face_assignments_people_PersonId",
                        column: x => x.PersonId,
                        principalTable: "people",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_person_face_assignments_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "face_cluster_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FaceClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    FaceDetectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SimilarityScore = table.Column<double>(type: "double precision", nullable: true),
                    MembershipSource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_face_cluster_members", x => x.Id);
                    table.ForeignKey(
                        name: "FK_face_cluster_members_face_clusters_FaceClusterId",
                        column: x => x.FaceClusterId,
                        principalTable: "face_clusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_face_cluster_members_face_detections_FaceDetectionId",
                        column: x => x.FaceDetectionId,
                        principalTable: "face_detections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_face_cluster_members_face",
                table: "face_cluster_members",
                column: "FaceDetectionId");

            migrationBuilder.CreateIndex(
                name: "ux_face_cluster_members_cluster_face",
                table: "face_cluster_members",
                columns: new[] { "FaceClusterId", "FaceDetectionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_face_clusters_owner_profile_status",
                table: "face_clusters",
                columns: new[] { "OwnerUserId", "ProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_face_clusters_PersonId",
                table: "face_clusters",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_face_clusters_ProfileId",
                table: "face_clusters",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "ix_people_owner_archived",
                table: "people",
                columns: new[] { "OwnerUserId", "IsArchived" });

            migrationBuilder.CreateIndex(
                name: "IX_person_face_assignments_FaceDetectionId",
                table: "person_face_assignments",
                column: "FaceDetectionId");

            migrationBuilder.CreateIndex(
                name: "ix_person_face_assignments_person",
                table: "person_face_assignments",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "ux_person_face_assignments_owner_face",
                table: "person_face_assignments",
                columns: new[] { "OwnerUserId", "FaceDetectionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_settings");

            migrationBuilder.DropTable(
                name: "face_cluster_members");

            migrationBuilder.DropTable(
                name: "person_face_assignments");

            migrationBuilder.DropTable(
                name: "face_clusters");

            migrationBuilder.DropTable(
                name: "people");
        }
    }
}
