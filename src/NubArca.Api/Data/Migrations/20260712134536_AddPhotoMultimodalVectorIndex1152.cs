using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoMultimodalVectorIndex1152 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder)) return;

            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'vector') THEN
        CREATE EXTENSION IF NOT EXISTS vector;

        CREATE TABLE IF NOT EXISTS blob_embedding_vectors_1152 (
            ""BlobEmbeddingId"" uuid PRIMARY KEY
                REFERENCES blob_embeddings(""Id"") ON DELETE CASCADE,
            ""BlobObjectId""    uuid NOT NULL,
            ""ProfileId""       uuid NOT NULL,
            embedding            vector(1152) NOT NULL,
            ""CreatedAt""       timestamp with time zone NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_bev1152_profile
            ON blob_embedding_vectors_1152 (""ProfileId"");
        CREATE INDEX IF NOT EXISTS ix_bev1152_blob_object
            ON blob_embedding_vectors_1152 (""BlobObjectId"");
        CREATE INDEX IF NOT EXISTS ix_bev1152_embedding_hnsw_cosine
            ON blob_embedding_vectors_1152
            USING hnsw (embedding vector_cosine_ops)
            WITH (m = 16, ef_construction = 96);
    ELSE
        RAISE WARNING 'pgvector extension not available; skipping blob_embedding_vectors_1152.';
    END IF;
END
$$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder)) return;
            migrationBuilder.Sql("DROP TABLE IF EXISTS blob_embedding_vectors_1152;");
        }

        private static bool IsNpgsql(MigrationBuilder migrationBuilder) =>
            migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }
}
