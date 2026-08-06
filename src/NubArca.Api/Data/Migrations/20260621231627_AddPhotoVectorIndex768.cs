using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoVectorIndex768 : Migration
    {
        // Phase 2B foundation: pgvector-backed photo-embedding similarity.
        //
        // PostgreSQL-only, additive, and fault-tolerant. This is a raw-SQL
        // "shadow" table NOT mapped in the EF model on purpose:
        //   * the EF model (and the SQLite EnsureCreated used by unit tests) stays
        //     unaware of the pgvector `vector` type, so tests are unaffected;
        //   * the canonical embedding storage (blob_embeddings.EmbeddingBytes) is
        //     unchanged and remains the source of truth + exact-scan fallback.
        //
        // The table holds 768-dim vectors only (SigLIP2 base). Other dimensions
        // get their OWN dimension-specific table/index in a future migration —
        // pgvector columns are fixed-dimension, so we never assume one global dim.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder))
            {
                return;
            }

            // Enable pgvector and create the 768-dim vector table + ANN index, but
            // ONLY when the `vector` extension is actually installable on this
            // server. On a non-pgvector image the whole block is skipped with a
            // WARNING, so the migration NEVER fails for a missing extension —
            // photo similarity then transparently uses the exact-scan fallback.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'vector') THEN
        CREATE EXTENSION IF NOT EXISTS vector;

        CREATE TABLE IF NOT EXISTS blob_embedding_vectors_768 (
            ""BlobEmbeddingId"" uuid PRIMARY KEY
                REFERENCES blob_embeddings(""Id"") ON DELETE CASCADE,
            ""BlobObjectId""    uuid NOT NULL,
            ""ProfileId""       uuid NOT NULL,
            embedding           vector(768) NOT NULL,
            ""CreatedAt""       timestamp with time zone NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_bev768_profile
            ON blob_embedding_vectors_768 (""ProfileId"");
        CREATE INDEX IF NOT EXISTS ix_bev768_blob_object
            ON blob_embedding_vectors_768 (""BlobObjectId"");

        -- HNSW (cosine) ANN index. Conservative build params; tune hnsw.ef_search
        -- at query time for recall. Built on an empty table => instant; rows added
        -- later by vector-sync are indexed incrementally.
        CREATE INDEX IF NOT EXISTS ix_bev768_embedding_hnsw_cosine
            ON blob_embedding_vectors_768
            USING hnsw (embedding vector_cosine_ops)
            WITH (m = 16, ef_construction = 64);
    ELSE
        RAISE WARNING 'pgvector extension not available; skipping blob_embedding_vectors_768 (photo similarity uses exact-scan fallback).';
    END IF;
END
$$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder))
            {
                return;
            }

            // Drop only the vector table; leave the `vector` extension in place
            // (other objects could depend on it, and dropping it is not required
            // to roll this migration back).
            migrationBuilder.Sql("DROP TABLE IF EXISTS blob_embedding_vectors_768;");
        }

        private static bool IsNpgsql(MigrationBuilder migrationBuilder) =>
            migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }
}
