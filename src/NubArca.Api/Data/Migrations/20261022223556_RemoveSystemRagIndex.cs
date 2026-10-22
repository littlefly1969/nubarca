using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSystemRagIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The system RAG index (product help and the NubArca repository) is
            // retired. Its pgvector mirror is raw SQL outside the EF model and
            // references rag_chunk_embeddings, so it goes first.
            if (IsNpgsql(migrationBuilder))
            {
                migrationBuilder.Sql("DROP TABLE IF EXISTS rag_chunk_embedding_vectors_384;");
            }

            migrationBuilder.DropTable(
                name: "rag_chunk_embeddings");

            migrationBuilder.DropTable(
                name: "rag_domain_sources");

            migrationBuilder.DropTable(
                name: "rag_chunks");

            migrationBuilder.DropTable(
                name: "rag_sources");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Recreates the tables empty. The pgvector mirror is not recreated:
            // the previous application reports semantic retrieval unavailable
            // without it, and a reindex would have to run anyway.
            migrationBuilder.CreateTable(
                name: "rag_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeLanguage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IndexFormatVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SourceKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rag_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rag_sources_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rag_chunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Heading = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    TextHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rag_chunks", x => x.Id);
                    table.CheckConstraint("ck_rag_chunks_ordinal_non_negative", "\"Ordinal\" >= 0");
                    table.ForeignKey(
                        name: "FK_rag_chunks_rag_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "rag_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rag_domain_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DomainKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rag_domain_sources", x => x.Id);
                    table.CheckConstraint("ck_rag_domain_sources_priority_range", "\"Priority\" >= 1 AND \"Priority\" <= 100");
                    table.ForeignKey(
                        name: "FK_rag_domain_sources_rag_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "rag_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rag_chunk_embeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChunkId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Dimension = table.Column<int>(type: "integer", nullable: false),
                    EmbeddingBytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rag_chunk_embeddings", x => x.Id);
                    table.CheckConstraint("ck_rag_chunk_embeddings_dimension_positive", "\"Dimension\" > 0");
                    table.ForeignKey(
                        name: "FK_rag_chunk_embeddings_ai_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rag_chunk_embeddings_rag_chunks_ChunkId",
                        column: x => x.ChunkId,
                        principalTable: "rag_chunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_rag_chunk_embeddings_profile",
                table: "rag_chunk_embeddings",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "ux_rag_chunk_embeddings_chunk_profile",
                table: "rag_chunk_embeddings",
                columns: new[] { "ChunkId", "ProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_rag_chunks_source_ordinal",
                table: "rag_chunks",
                columns: new[] { "SourceId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rag_domain_sources_domain",
                table: "rag_domain_sources",
                column: "DomainKey");

            migrationBuilder.CreateIndex(
                name: "ix_rag_domain_sources_domain_revision",
                table: "rag_domain_sources",
                columns: new[] { "DomainKey", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_rag_domain_sources_SourceId",
                table: "rag_domain_sources",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "ux_rag_domain_sources_domain_source",
                table: "rag_domain_sources",
                columns: new[] { "DomainKey", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rag_sources_key",
                table: "rag_sources",
                column: "SourceKey");

            migrationBuilder.CreateIndex(
                name: "ix_rag_sources_owner",
                table: "rag_sources",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "ux_rag_sources_key_content_format",
                table: "rag_sources",
                columns: new[] { "SourceKey", "ContentHash", "IndexFormatVersion" },
                unique: true);
        }

        private static bool IsNpgsql(MigrationBuilder migrationBuilder) =>
            migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }
}
