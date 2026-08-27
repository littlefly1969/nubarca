using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRagVectorIndex384 : Migration
    {
        // The pgvector ACCELERATOR for RAG chunk embeddings. Canonical vectors
        // live in rag_chunk_embeddings.EmbeddingBytes; this table is derived and
        // can be rebuilt from them at any time.
        //
        // Raw SQL, guarded, and its own migration — the same pattern the photo
        // and face vector tables use — because the `vector` type is deliberately
        // never mapped in the EF model. That is what lets SQLite unit tests and
        // a Postgres without the extension run the whole retrieval stack and
        // simply report the vector backend unavailable.
        //
        // ONE dimension per table, and 384 is this version's text-embedding
        // model. Another model means another table: there is no truncation and
        // no padding into this one, because a coerced vector is not the vector
        // the model produced and nothing downstream would notice.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder)) return;

            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'vector') THEN
        CREATE EXTENSION IF NOT EXISTS vector;

        CREATE TABLE IF NOT EXISTS rag_chunk_embedding_vectors_384 (
            ""EmbeddingId"" uuid PRIMARY KEY
                REFERENCES rag_chunk_embeddings(""Id"") ON DELETE CASCADE,
            ""ChunkId""     uuid NOT NULL
                REFERENCES rag_chunks(""Id"") ON DELETE CASCADE,
            ""ProfileId""   uuid NOT NULL,
            embedding        vector(384) NOT NULL,
            ""CreatedAt""   timestamp with time zone NOT NULL
        );

        -- Every read filters by profile; the domain filter joins through
        -- ChunkId, so both columns are indexed.
        CREATE INDEX IF NOT EXISTS ix_rcev384_profile
            ON rag_chunk_embedding_vectors_384 (""ProfileId"");
        CREATE INDEX IF NOT EXISTS ix_rcev384_chunk
            ON rag_chunk_embedding_vectors_384 (""ChunkId"");
        CREATE INDEX IF NOT EXISTS ix_rcev384_embedding_hnsw_cosine
            ON rag_chunk_embedding_vectors_384
            USING hnsw (embedding vector_cosine_ops)
            WITH (m = 16, ef_construction = 96);
    ELSE
        RAISE WARNING 'pgvector extension not available; skipping rag_chunk_embedding_vectors_384.';
    END IF;
END
$$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder)) return;
            migrationBuilder.Sql("DROP TABLE IF EXISTS rag_chunk_embedding_vectors_384;");
        }

        private static bool IsNpgsql(MigrationBuilder migrationBuilder) =>
            migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }
}
