using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiSubstrate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_index_diagnostics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Capability = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BlobObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    DocumentChunkId = table.Column<Guid>(type: "uuid", nullable: true),
                    FaceDetectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsPermanent = table.Column<bool>(type: "boolean", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    SanitizedMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_index_diagnostics", x => x.Id);
                    table.CheckConstraint("ck_ai_index_diagnostics_attempt_count_non_negative", "\"AttemptCount\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "ai_models",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Capability = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Modality = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Dimension = table.Column<int>(type: "integer", nullable: true),
                    DistanceMetric = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_models", x => x.Id);
                    table.CheckConstraint("ck_ai_models_dimension_positive", "\"Dimension\" IS NULL OR \"Dimension\" > 0");
                    table.CheckConstraint("ck_ai_models_version_positive", "\"Version\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "ai_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AiModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Capability = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Modality = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Dimension = table.Column<int>(type: "integer", nullable: true),
                    DistanceMetric = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ConfigHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_profiles", x => x.Id);
                    table.CheckConstraint("ck_ai_profiles_dimension_positive", "\"Dimension\" IS NULL OR \"Dimension\" > 0");
                    table.ForeignKey(
                        name: "FK_ai_profiles_ai_models_AiModelId",
                        column: x => x.AiModelId,
                        principalTable: "ai_models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_annotations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Text = table.Column<string>(type: "text", nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_annotations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_annotations_ai_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ai_annotations_file_items_FileItemId",
                        column: x => x.FileItemId,
                        principalTable: "file_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ai_annotations_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "blob_ai_artifact_statuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BlobObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Capability = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsPermanent = table.Column<bool>(type: "boolean", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blob_ai_artifact_statuses", x => x.Id);
                    table.CheckConstraint("ck_blob_ai_artifact_statuses_attempt_count_non_negative", "\"AttemptCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_blob_ai_artifact_statuses_ai_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_blob_ai_artifact_statuses_blob_objects_BlobObjectId",
                        column: x => x.BlobObjectId,
                        principalTable: "blob_objects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "blob_embeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BlobObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmbeddingBytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    Dimension = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blob_embeddings", x => x.Id);
                    table.CheckConstraint("ck_blob_embeddings_dimension_positive", "\"Dimension\" > 0");
                    table.ForeignKey(
                        name: "FK_blob_embeddings_ai_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_blob_embeddings_blob_objects_BlobObjectId",
                        column: x => x.BlobObjectId,
                        principalTable: "blob_objects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_texts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TextHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Text = table.Column<string>(type: "text", nullable: true),
                    CharCount = table.Column<int>(type: "integer", nullable: true),
                    Language = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_texts", x => x.Id);
                    table.CheckConstraint("ck_document_texts_char_count_non_negative", "\"CharCount\" IS NULL OR \"CharCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_document_texts_ai_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_document_texts_file_items_FileItemId",
                        column: x => x.FileItemId,
                        principalTable: "file_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_document_texts_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "face_detections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoundingBoxX = table.Column<double>(type: "double precision", nullable: false),
                    BoundingBoxY = table.Column<double>(type: "double precision", nullable: false),
                    BoundingBoxWidth = table.Column<double>(type: "double precision", nullable: false),
                    BoundingBoxHeight = table.Column<double>(type: "double precision", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_face_detections", x => x.Id);
                    table.CheckConstraint("ck_face_detections_box_height_non_negative", "\"BoundingBoxHeight\" >= 0");
                    table.CheckConstraint("ck_face_detections_box_width_non_negative", "\"BoundingBoxWidth\" >= 0");
                    table.ForeignKey(
                        name: "FK_face_detections_ai_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_face_detections_file_items_FileItemId",
                        column: x => x.FileItemId,
                        principalTable: "file_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_face_detections_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "person_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ClusterKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_person_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_person_groups_ai_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_person_groups_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_chunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentTextId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: true),
                    TextHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    StartOffset = table.Column<int>(type: "integer", nullable: true),
                    EndOffset = table.Column<int>(type: "integer", nullable: true),
                    Page = table.Column<int>(type: "integer", nullable: true),
                    TokenCount = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_chunks", x => x.Id);
                    table.CheckConstraint("ck_document_chunks_ordinal_non_negative", "\"Ordinal\" >= 0");
                    table.ForeignKey(
                        name: "FK_document_chunks_ai_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_document_chunks_document_texts_DocumentTextId",
                        column: x => x.DocumentTextId,
                        principalTable: "document_texts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_document_chunks_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "face_embeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FaceDetectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmbeddingBytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    Dimension = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_face_embeddings", x => x.Id);
                    table.CheckConstraint("ck_face_embeddings_dimension_positive", "\"Dimension\" > 0");
                    table.ForeignKey(
                        name: "FK_face_embeddings_ai_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_face_embeddings_face_detections_FaceDetectionId",
                        column: x => x.FaceDetectionId,
                        principalTable: "face_detections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "face_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FaceDetectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    FaceEmbeddingProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_face_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_face_assignments_ai_profiles_FaceEmbeddingProfileId",
                        column: x => x.FaceEmbeddingProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_face_assignments_face_detections_FaceDetectionId",
                        column: x => x.FaceDetectionId,
                        principalTable: "face_detections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_face_assignments_person_groups_PersonGroupId",
                        column: x => x.PersonGroupId,
                        principalTable: "person_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_face_assignments_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_chunk_embeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentChunkId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmbeddingBytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    Dimension = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_chunk_embeddings", x => x.Id);
                    table.CheckConstraint("ck_document_chunk_embeddings_dimension_positive", "\"Dimension\" > 0");
                    table.ForeignKey(
                        name: "FK_document_chunk_embeddings_ai_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "ai_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_document_chunk_embeddings_document_chunks_DocumentChunkId",
                        column: x => x.DocumentChunkId,
                        principalTable: "document_chunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_annotations_file_profile_kind",
                table: "ai_annotations",
                columns: new[] { "FileItemId", "ProfileId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_annotations_owner",
                table: "ai_annotations",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_annotations_ProfileId",
                table: "ai_annotations",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "ix_ai_index_diagnostics_capability_profile_error",
                table: "ai_index_diagnostics",
                columns: new[] { "Capability", "ProfileId", "ErrorCode" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_index_diagnostics_capability_target_kind",
                table: "ai_index_diagnostics",
                columns: new[] { "Capability", "TargetKind" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_index_diagnostics_owner_capability",
                table: "ai_index_diagnostics",
                columns: new[] { "OwnerUserId", "Capability" });

            migrationBuilder.CreateIndex(
                name: "ux_ai_models_key",
                table: "ai_models",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_profiles_AiModelId",
                table: "ai_profiles",
                column: "AiModelId");

            migrationBuilder.CreateIndex(
                name: "ix_ai_profiles_capability_default",
                table: "ai_profiles",
                columns: new[] { "Capability", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "ux_ai_profiles_capability_default_active",
                table: "ai_profiles",
                column: "Capability",
                unique: true,
                filter: "\"IsDefault\"");

            migrationBuilder.CreateIndex(
                name: "ux_ai_profiles_key",
                table: "ai_profiles",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_blob_ai_artifact_statuses_profile_status",
                table: "blob_ai_artifact_statuses",
                columns: new[] { "ProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ux_blob_ai_artifact_statuses_blob_profile_capability",
                table: "blob_ai_artifact_statuses",
                columns: new[] { "BlobObjectId", "ProfileId", "Capability" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_blob_embeddings_ProfileId",
                table: "blob_embeddings",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "ux_blob_embeddings_blob_profile",
                table: "blob_embeddings",
                columns: new[] { "BlobObjectId", "ProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_chunk_embeddings_ProfileId",
                table: "document_chunk_embeddings",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "ux_document_chunk_embeddings_chunk_profile",
                table: "document_chunk_embeddings",
                columns: new[] { "DocumentChunkId", "ProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_chunks_owner",
                table: "document_chunks",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_document_chunks_ProfileId",
                table: "document_chunks",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "ux_document_chunks_doc_profile_ordinal",
                table: "document_chunks",
                columns: new[] { "DocumentTextId", "ProfileId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_texts_owner",
                table: "document_texts",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_document_texts_ProfileId",
                table: "document_texts",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "ux_document_texts_file_profile",
                table: "document_texts",
                columns: new[] { "FileItemId", "ProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_face_assignments_FaceDetectionId",
                table: "face_assignments",
                column: "FaceDetectionId");

            migrationBuilder.CreateIndex(
                name: "IX_face_assignments_FaceEmbeddingProfileId",
                table: "face_assignments",
                column: "FaceEmbeddingProfileId");

            migrationBuilder.CreateIndex(
                name: "ix_face_assignments_person_group",
                table: "face_assignments",
                column: "PersonGroupId");

            migrationBuilder.CreateIndex(
                name: "ux_face_assignments_owner_face_profile",
                table: "face_assignments",
                columns: new[] { "OwnerUserId", "FaceDetectionId", "FaceEmbeddingProfileId" },
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_face_embeddings_ProfileId",
                table: "face_embeddings",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "ux_face_embeddings_detection_profile",
                table: "face_embeddings",
                columns: new[] { "FaceDetectionId", "ProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_person_groups_owner_profile",
                table: "person_groups",
                columns: new[] { "OwnerUserId", "ProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_person_groups_ProfileId",
                table: "person_groups",
                column: "ProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_annotations");

            migrationBuilder.DropTable(
                name: "ai_index_diagnostics");

            migrationBuilder.DropTable(
                name: "blob_ai_artifact_statuses");

            migrationBuilder.DropTable(
                name: "blob_embeddings");

            migrationBuilder.DropTable(
                name: "document_chunk_embeddings");

            migrationBuilder.DropTable(
                name: "face_assignments");

            migrationBuilder.DropTable(
                name: "face_embeddings");

            migrationBuilder.DropTable(
                name: "document_chunks");

            migrationBuilder.DropTable(
                name: "person_groups");

            migrationBuilder.DropTable(
                name: "face_detections");

            migrationBuilder.DropTable(
                name: "document_texts");

            migrationBuilder.DropTable(
                name: "ai_profiles");

            migrationBuilder.DropTable(
                name: "ai_models");
        }
    }
}
