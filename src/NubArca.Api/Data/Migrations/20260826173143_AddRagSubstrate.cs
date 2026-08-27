using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRagSubstrate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rag_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SourceKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Revision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CodeLanguage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
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
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Heading = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    TextHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    DomainKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
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
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmbeddingBytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    Dimension = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "IX_rag_domain_sources_SourceId",
                table: "rag_domain_sources",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "ux_rag_domain_sources_domain_source",
                table: "rag_domain_sources",
                columns: new[] { "DomainKey", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rag_sources_owner",
                table: "rag_sources",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "ix_rag_sources_revision",
                table: "rag_sources",
                column: "Revision");

            migrationBuilder.CreateIndex(
                name: "ux_rag_sources_key",
                table: "rag_sources",
                column: "SourceKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rag_chunk_embeddings");

            migrationBuilder.DropTable(
                name: "rag_domain_sources");

            migrationBuilder.DropTable(
                name: "rag_chunks");

            migrationBuilder.DropTable(
                name: "rag_sources");
        }
    }
}
