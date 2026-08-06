using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFaceSubstrateV0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_face_detections_file_items_FileItemId",
                table: "face_detections");

            migrationBuilder.DropForeignKey(
                name: "FK_face_detections_users_OwnerUserId",
                table: "face_detections");

            migrationBuilder.DropIndex(
                name: "ix_face_detections_file_profile",
                table: "face_detections");

            migrationBuilder.DropIndex(
                name: "ix_face_detections_owner",
                table: "face_detections");

            migrationBuilder.DropIndex(
                name: "IX_face_detections_ProfileId",
                table: "face_detections");

            migrationBuilder.DropColumn(
                name: "FileItemId",
                table: "face_detections");

            migrationBuilder.RenameColumn(
                name: "OwnerUserId",
                table: "face_detections",
                newName: "BlobObjectId");

            migrationBuilder.RenameColumn(
                name: "Confidence",
                table: "face_detections",
                newName: "FaceQualityScore");

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingStatus",
                table: "face_embeddings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "completed");

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "face_embeddings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DetectionScore",
                table: "face_detections",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DetectorProfileKey",
                table: "face_detections",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FaceIndex",
                table: "face_detections",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LandmarksJson",
                table: "face_detections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "face_detections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_face_detections_profile_blob",
                table: "face_detections",
                columns: new[] { "ProfileId", "BlobObjectId" });

            migrationBuilder.CreateIndex(
                name: "ux_face_detections_blob_profile_index",
                table: "face_detections",
                columns: new[] { "BlobObjectId", "ProfileId", "FaceIndex" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_face_detections_face_index_non_negative",
                table: "face_detections",
                sql: "\"FaceIndex\" >= 0");

            migrationBuilder.AddForeignKey(
                name: "FK_face_detections_blob_objects_BlobObjectId",
                table: "face_detections",
                column: "BlobObjectId",
                principalTable: "blob_objects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            CreateFaceVectorTable(migrationBuilder);
        }

        // Face Substrate v0: pgvector-backed 512-dim ArcFace face-embedding search.
        // PostgreSQL-only, additive, fault-tolerant — a raw-SQL "shadow" table NOT
        // mapped in the EF model (so SQLite unit tests / non-pgvector Postgres are
        // unaffected and the canonical FaceEmbedding.EmbeddingBytes stays the source
        // of truth + exact-scan fallback). Fixed dimension 512; other dimensions get
        // their own future table. Created empty ⇒ instant HNSW build.
        private static void CreateFaceVectorTable(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder))
            {
                return;
            }

            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'vector') THEN
        CREATE EXTENSION IF NOT EXISTS vector;

        CREATE TABLE IF NOT EXISTS face_embedding_vectors_512 (
            ""FaceEmbeddingId""  uuid PRIMARY KEY
                REFERENCES face_embeddings(""Id"") ON DELETE CASCADE,
            ""FaceDetectionId"" uuid NOT NULL,
            ""BlobObjectId""    uuid NOT NULL,
            ""ProfileId""       uuid NOT NULL,
            embedding           vector(512) NOT NULL,
            ""CreatedAt""       timestamp with time zone NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_fev512_profile
            ON face_embedding_vectors_512 (""ProfileId"");
        CREATE INDEX IF NOT EXISTS ix_fev512_detection
            ON face_embedding_vectors_512 (""FaceDetectionId"");
        CREATE INDEX IF NOT EXISTS ix_fev512_blob_object
            ON face_embedding_vectors_512 (""BlobObjectId"");

        CREATE INDEX IF NOT EXISTS ix_fev512_embedding_hnsw_cosine
            ON face_embedding_vectors_512
            USING hnsw (embedding vector_cosine_ops)
            WITH (m = 16, ef_construction = 64);
    ELSE
        RAISE WARNING 'pgvector extension not available; skipping face_embedding_vectors_512 (face search uses pgvector only when available).';
    END IF;
END
$$;");
        }

        private static bool IsNpgsql(MigrationBuilder migrationBuilder) =>
            migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (IsNpgsql(migrationBuilder))
            {
                migrationBuilder.Sql("DROP TABLE IF EXISTS face_embedding_vectors_512;");
            }

            migrationBuilder.DropForeignKey(
                name: "FK_face_detections_blob_objects_BlobObjectId",
                table: "face_detections");

            migrationBuilder.DropIndex(
                name: "ix_face_detections_profile_blob",
                table: "face_detections");

            migrationBuilder.DropIndex(
                name: "ux_face_detections_blob_profile_index",
                table: "face_detections");

            migrationBuilder.DropCheckConstraint(
                name: "ck_face_detections_face_index_non_negative",
                table: "face_detections");

            migrationBuilder.DropColumn(
                name: "EmbeddingStatus",
                table: "face_embeddings");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "face_embeddings");

            migrationBuilder.DropColumn(
                name: "DetectionScore",
                table: "face_detections");

            migrationBuilder.DropColumn(
                name: "DetectorProfileKey",
                table: "face_detections");

            migrationBuilder.DropColumn(
                name: "FaceIndex",
                table: "face_detections");

            migrationBuilder.DropColumn(
                name: "LandmarksJson",
                table: "face_detections");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "face_detections");

            migrationBuilder.RenameColumn(
                name: "FaceQualityScore",
                table: "face_detections",
                newName: "Confidence");

            migrationBuilder.RenameColumn(
                name: "BlobObjectId",
                table: "face_detections",
                newName: "OwnerUserId");

            migrationBuilder.AddColumn<Guid>(
                name: "FileItemId",
                table: "face_detections",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "ix_face_detections_file_profile",
                table: "face_detections",
                columns: new[] { "FileItemId", "ProfileId" });

            migrationBuilder.CreateIndex(
                name: "ix_face_detections_owner",
                table: "face_detections",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_face_detections_ProfileId",
                table: "face_detections",
                column: "ProfileId");

            migrationBuilder.AddForeignKey(
                name: "FK_face_detections_file_items_FileItemId",
                table: "face_detections",
                column: "FileItemId",
                principalTable: "file_items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_face_detections_users_OwnerUserId",
                table: "face_detections",
                column: "OwnerUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
