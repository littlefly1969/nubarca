using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoSemanticSampleEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "video_semantic_embedding_statuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoSemanticIndexId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExpectedSampleCount = table.Column<int>(type: "integer", nullable: false),
                    CompletedSampleCount = table.Column<int>(type: "integer", nullable: false),
                    FailedSampleCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_semantic_embedding_statuses", x => x.Id);
                    table.CheckConstraint("ck_video_semantic_embedding_statuses_counts_non_negative", "\"ExpectedSampleCount\" >= 0 AND \"CompletedSampleCount\" >= 0 AND \"FailedSampleCount\" >= 0 AND \"AttemptCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_video_semantic_embedding_statuses_ai_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_video_semantic_embedding_statuses_video_semantic_indexes_Vi~",
                        column: x => x.VideoSemanticIndexId,
                        principalTable: "video_semantic_indexes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "video_semantic_sample_embeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoSemanticSampleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmbeddingBytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    Dimension = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_semantic_sample_embeddings", x => x.Id);
                    table.CheckConstraint("ck_video_semantic_sample_embeddings_attempts_non_negative", "\"AttemptCount\" >= 0");
                    table.CheckConstraint("ck_video_semantic_sample_embeddings_dimension_non_negative", "\"Dimension\" >= 0");
                    table.ForeignKey(
                        name: "FK_video_semantic_sample_embeddings_ai_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_video_semantic_sample_embeddings_video_semantic_samples_Vid~",
                        column: x => x.VideoSemanticSampleId,
                        principalTable: "video_semantic_samples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_video_semantic_embedding_statuses_profile_status",
                table: "video_semantic_embedding_statuses",
                columns: new[] { "ProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ux_video_semantic_embedding_statuses_index_profile",
                table: "video_semantic_embedding_statuses",
                columns: new[] { "VideoSemanticIndexId", "ProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_video_semantic_sample_embeddings_profile_status",
                table: "video_semantic_sample_embeddings",
                columns: new[] { "ProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ux_video_semantic_sample_embeddings_sample_profile",
                table: "video_semantic_sample_embeddings",
                columns: new[] { "VideoSemanticSampleId", "ProfileId" },
                unique: true);

            // Optional pgvector ACCELERATION layer (same pattern as
            // blob_embedding_vectors_1152). The canonical rows above are the
            // source of truth; when the extension is unavailable the table is
            // simply skipped and the read path uses exact-scan.
            if (IsNpgsql(migrationBuilder))
            {
                migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'vector') THEN
        CREATE EXTENSION IF NOT EXISTS vector;

        CREATE TABLE IF NOT EXISTS video_semantic_sample_embedding_vectors_1152 (
            ""VideoSemanticSampleEmbeddingId"" uuid PRIMARY KEY
                REFERENCES video_semantic_sample_embeddings(""Id"") ON DELETE CASCADE,
            ""VideoSemanticSampleId""          uuid NOT NULL,
            ""ProfileId""                      uuid NOT NULL,
            embedding                           vector(1152) NOT NULL,
            ""CreatedAt""                      timestamp with time zone NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_vsev1152_profile
            ON video_semantic_sample_embedding_vectors_1152 (""ProfileId"");
        CREATE INDEX IF NOT EXISTS ix_vsev1152_sample
            ON video_semantic_sample_embedding_vectors_1152 (""VideoSemanticSampleId"");
        CREATE INDEX IF NOT EXISTS ix_vsev1152_embedding_hnsw_cosine
            ON video_semantic_sample_embedding_vectors_1152
            USING hnsw (embedding vector_cosine_ops)
            WITH (m = 16, ef_construction = 96);
    ELSE
        RAISE WARNING 'pgvector extension not available; skipping video_semantic_sample_embedding_vectors_1152.';
    END IF;
END
$$;");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (IsNpgsql(migrationBuilder))
            {
                migrationBuilder.Sql(
                    "DROP TABLE IF EXISTS video_semantic_sample_embedding_vectors_1152;");
            }

            migrationBuilder.DropTable(
                name: "video_semantic_embedding_statuses");

            migrationBuilder.DropTable(
                name: "video_semantic_sample_embeddings");
        }

        private static bool IsNpgsql(MigrationBuilder migrationBuilder) =>
            migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }
}
