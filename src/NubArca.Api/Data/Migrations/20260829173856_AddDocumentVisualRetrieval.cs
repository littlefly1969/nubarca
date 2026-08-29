using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentVisualRetrieval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_visual_indexes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceBlobObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RenderProfileKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EmbeddingProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UnitCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_visual_indexes", x => x.Id);
                    table.CheckConstraint("ck_document_visual_indexes_completed_has_units", "\"Status\" <> 'completed' OR \"UnitCount\" > 0");
                    table.CheckConstraint("ck_document_visual_indexes_unit_count_non_negative", "\"UnitCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_document_visual_indexes_ai_profiles_EmbeddingProfileId",
                        column: x => x.EmbeddingProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_document_visual_indexes_file_items_FileItemId",
                        column: x => x.FileItemId,
                        principalTable: "file_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_document_visual_indexes_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_visual_units",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentVisualIndexId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    RenderKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceLocatorKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    SourceLocatorIndex = table.Column<int>(type: "integer", nullable: true),
                    SourceLocatorLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SourcePage = table.Column<int>(type: "integer", nullable: true),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    PixelHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_visual_units", x => x.Id);
                    table.CheckConstraint("ck_document_visual_units_dimensions_positive", "\"Width\" > 0 AND \"Height\" > 0");
                    table.CheckConstraint("ck_document_visual_units_ordinal_non_negative", "\"Ordinal\" >= 0");
                    table.CheckConstraint("ck_document_visual_units_source_page_positive", "\"SourcePage\" IS NULL OR \"SourcePage\" >= 1");
                    table.ForeignKey(
                        name: "FK_document_visual_units_document_visual_indexes_DocumentVisua~",
                        column: x => x.DocumentVisualIndexId,
                        principalTable: "document_visual_indexes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_visual_embeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentVisualUnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Layout = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Dimension = table.Column<int>(type: "integer", nullable: false),
                    VectorCount = table.Column<int>(type: "integer", nullable: false),
                    EmbeddingBytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_visual_embeddings", x => x.Id);
                    table.CheckConstraint("ck_document_visual_embeddings_dense_is_single", "\"Layout\" <> 'dense' OR \"VectorCount\" = 1");
                    table.CheckConstraint("ck_document_visual_embeddings_dimension_positive", "\"Dimension\" > 0");
                    table.CheckConstraint("ck_document_visual_embeddings_vector_count_positive", "\"VectorCount\" > 0");
                    table.ForeignKey(
                        name: "FK_document_visual_embeddings_ai_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_document_visual_embeddings_document_visual_units_DocumentVi~",
                        column: x => x.DocumentVisualUnitId,
                        principalTable: "document_visual_units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_visual_embeddings_ProfileId",
                table: "document_visual_embeddings",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "ux_document_visual_embeddings_unit_profile",
                table: "document_visual_embeddings",
                columns: new[] { "DocumentVisualUnitId", "ProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_visual_indexes_EmbeddingProfileId",
                table: "document_visual_indexes",
                column: "EmbeddingProfileId");

            migrationBuilder.CreateIndex(
                name: "ix_document_visual_indexes_owner",
                table: "document_visual_indexes",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "ix_document_visual_indexes_owner_status_profile",
                table: "document_visual_indexes",
                columns: new[] { "OwnerUserId", "Status", "EmbeddingProfileId" });

            migrationBuilder.CreateIndex(
                name: "ix_document_visual_indexes_source_blob",
                table: "document_visual_indexes",
                column: "SourceBlobObjectId");

            migrationBuilder.CreateIndex(
                name: "ux_document_visual_indexes_file_blob_render_profile",
                table: "document_visual_indexes",
                columns: new[] { "FileItemId", "SourceBlobObjectId", "RenderProfileKey", "EmbeddingProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_document_visual_units_index_ordinal",
                table: "document_visual_units",
                columns: new[] { "DocumentVisualIndexId", "Ordinal" },
                unique: true);

            AddDenseVectorAccelerator(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DropDenseVectorAccelerator(migrationBuilder);

            migrationBuilder.DropTable(
                name: "document_visual_embeddings");

            migrationBuilder.DropTable(
                name: "document_visual_units");

            migrationBuilder.DropTable(
                name: "document_visual_indexes");
        }
    
        // ---- the dense pgvector accelerator -------------------------------
        //
        // CANONICAL BYTES STAY THE TRUTH. document_visual_embeddings.EmbeddingBytes
        // is durable; this table is derived from it and can be dropped and
        // rebuilt at any time. Raw SQL, guarded, and the `vector` type is never
        // mapped in the EF model — the same pattern the photo, face and RAG
        // vector tables use, and what lets SQLite unit tests and a PostgreSQL
        // without the extension run the whole retrieval stack while simply
        // reporting the accelerator unavailable.
        //
        // THERE IS DELIBERATELY NO HNSW INDEX HERE, and that is the whole design
        // rather than an omission.
        //
        // An approximate index plus `WHERE OwnerUserId = …` is NOT an
        // owner-prefiltered nearest-neighbour search: the graph is traversed over
        // every owner's vectors and the predicate filters whatever the traversal
        // happens to surface, so a person with few documents in a large
        // installation silently gets fewer and worse results — the failure mode
        // OwnerDocumentVectorRetriever already refuses for text. With no ANN
        // index the planner has one option: restrict to this owner's eligible
        // units through the joins, then rank exactly. That is the guarantee
        // section 26 of the specification asks for, stated as an absence.
        //
        // What the table still buys is real: the cosine is computed in
        // PostgreSQL over the filtered rows instead of shipping every candidate's
        // 4.6 KiB of float32 to the application for every question.
        //
        // ONE DIMENSION PER TABLE. 1152 is SigLIP2 So400m; another model means
        // another table, never a truncation or a pad into this one.
        private static void AddDenseVectorAccelerator(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder)) return;

            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'vector') THEN
        CREATE EXTENSION IF NOT EXISTS vector;

        CREATE TABLE IF NOT EXISTS document_visual_embedding_vectors_1152 (
            ""EmbeddingId""         uuid PRIMARY KEY
                REFERENCES document_visual_embeddings(""Id"") ON DELETE CASCADE,
            ""DocumentVisualUnitId"" uuid NOT NULL
                REFERENCES document_visual_units(""Id"") ON DELETE CASCADE,
            ""ProfileId""           uuid NOT NULL,
            embedding                vector(1152) NOT NULL,
            ""CreatedAt""           timestamp with time zone NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_dvev1152_profile
            ON document_visual_embedding_vectors_1152 (""ProfileId"");
        CREATE INDEX IF NOT EXISTS ix_dvev1152_unit
            ON document_visual_embedding_vectors_1152 (""DocumentVisualUnitId"");
    ELSE
        RAISE WARNING 'pgvector extension not available; skipping document_visual_embedding_vectors_1152.';
    END IF;
END
$$;");
        }

        private static void DropDenseVectorAccelerator(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder)) return;
            migrationBuilder.Sql("DROP TABLE IF EXISTS document_visual_embedding_vectors_1152;");
        }

        private static bool IsNpgsql(MigrationBuilder migrationBuilder) =>
            migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }
}
