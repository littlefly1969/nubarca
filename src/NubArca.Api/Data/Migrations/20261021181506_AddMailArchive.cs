using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMailArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "mail_archives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FingerprintScheme = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceFileItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SourceSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ScanCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MessageCount = table.Column<int>(type: "integer", nullable: false),
                    FolderCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_archives", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mail_archives_file_items_SourceFileItemId",
                        column: x => x.SourceFileItemId,
                        principalTable: "file_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_mail_archives_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mail_deletion_tombstones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceMessageKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DeletedMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_deletion_tombstones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mail_deletion_tombstones_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mail_folders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchiveId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceFolderKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PathJson = table.Column<string>(type: "text", nullable: false),
                    Depth = table.Column<int>(type: "integer", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    ContainerClass = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    MessageCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_folders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mail_folders_mail_archives_ArchiveId",
                        column: x => x.ArchiveId,
                        principalTable: "mail_archives",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_mail_folders_mail_folders_ParentId",
                        column: x => x.ParentId,
                        principalTable: "mail_folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mail_import_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchiveId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceMessageKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceFolderKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastOutcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_import_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mail_import_items_mail_archives_ArchiveId",
                        column: x => x.ArchiveId,
                        principalTable: "mail_archives",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mail_import_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchiveId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Phase = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TotalMessages = table.Column<int>(type: "integer", nullable: false),
                    ProcessedMessages = table.Column<int>(type: "integer", nullable: false),
                    ImportedMessages = table.Column<int>(type: "integer", nullable: false),
                    AlreadyPresentMessages = table.Column<int>(type: "integer", nullable: false),
                    SkippedDeletedMessages = table.Column<int>(type: "integer", nullable: false),
                    FailedMessages = table.Column<int>(type: "integer", nullable: false),
                    AttachmentsCreated = table.Column<int>(type: "integer", nullable: false),
                    AttachmentsReused = table.Column<int>(type: "integer", nullable: false),
                    AttachmentsSkipped = table.Column<int>(type: "integer", nullable: false),
                    ScanErrors = table.Column<int>(type: "integer", nullable: false),
                    ScannedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ScanIssuesJson = table.Column<string>(type: "text", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_import_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mail_import_runs_mail_archives_ArchiveId",
                        column: x => x.ArchiveId,
                        principalTable: "mail_archives",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mail_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchiveId = table.Column<Guid>(type: "uuid", nullable: false),
                    FolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceMessageKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BlobObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    DocumentFormat = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DocumentVersion = table.Column<int>(type: "integer", nullable: false),
                    DocumentSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    MessageClass = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Subject = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FromName = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    FromAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    ToSummary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Preview = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SortDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InternetMessageId = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: true),
                    HasAttachments = table.Column<bool>(type: "boolean", nullable: false),
                    AttachmentCount = table.Column<int>(type: "integer", nullable: false),
                    SearchSubject = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SearchFrom = table.Column<string>(type: "character varying(700)", maxLength: 700, nullable: false),
                    SearchRecipients = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mail_messages_blob_objects_BlobObjectId",
                        column: x => x.BlobObjectId,
                        principalTable: "blob_objects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_mail_messages_mail_archives_ArchiveId",
                        column: x => x.ArchiveId,
                        principalTable: "mail_archives",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_mail_messages_mail_folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "mail_folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mail_attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttachmentKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    OriginalName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ContentId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IsInline = table.Column<bool>(type: "boolean", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SkipReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FileItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mail_attachments_file_items_FileItemId",
                        column: x => x.FileItemId,
                        principalTable: "file_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_mail_attachments_mail_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "mail_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mail_archives_source_file",
                table: "mail_archives",
                column: "SourceFileItemId");

            migrationBuilder.CreateIndex(
                name: "ux_mail_archives_owner_fingerprint",
                table: "mail_archives",
                columns: new[] { "OwnerUserId", "SourceFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mail_attachments_file_item",
                table: "mail_attachments",
                column: "FileItemId");

            migrationBuilder.CreateIndex(
                name: "ux_mail_attachments_message_key",
                table: "mail_attachments",
                columns: new[] { "MessageId", "AttachmentKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mail_tombstones_owner_message",
                table: "mail_deletion_tombstones",
                columns: new[] { "OwnerUserId", "DeletedMessageId" });

            migrationBuilder.CreateIndex(
                name: "ux_mail_tombstones_owner_source_message",
                table: "mail_deletion_tombstones",
                columns: new[] { "OwnerUserId", "SourceFingerprint", "SourceMessageKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mail_folders_archive_ordinal",
                table: "mail_folders",
                columns: new[] { "ArchiveId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_mail_folders_ParentId",
                table: "mail_folders",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "ux_mail_folders_archive_source",
                table: "mail_folders",
                columns: new[] { "ArchiveId", "SourceFolderKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mail_import_items_archive_ordinal",
                table: "mail_import_items",
                columns: new[] { "ArchiveId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "ix_mail_import_items_run_outcome",
                table: "mail_import_items",
                columns: new[] { "LastRunId", "LastOutcome" });

            migrationBuilder.CreateIndex(
                name: "ux_mail_import_items_archive_message",
                table: "mail_import_items",
                columns: new[] { "ArchiveId", "SourceMessageKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mail_import_runs_archive_created",
                table: "mail_import_runs",
                columns: new[] { "ArchiveId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_mail_import_runs_one_active",
                table: "mail_import_runs",
                column: "ArchiveId",
                unique: true,
                filter: "\"Status\" IN ('queued', 'running')");

            migrationBuilder.CreateIndex(
                name: "ix_mail_messages_archive_date",
                table: "mail_messages",
                columns: new[] { "ArchiveId", "SortDate", "Id" });

            migrationBuilder.CreateIndex(
                name: "ix_mail_messages_blob_object",
                table: "mail_messages",
                column: "BlobObjectId");

            migrationBuilder.CreateIndex(
                name: "ix_mail_messages_folder_date",
                table: "mail_messages",
                columns: new[] { "FolderId", "SortDate", "Id" });

            migrationBuilder.CreateIndex(
                name: "ix_mail_messages_owner_date",
                table: "mail_messages",
                columns: new[] { "OwnerUserId", "SortDate", "Id" });

            migrationBuilder.CreateIndex(
                name: "ix_mail_messages_search_from_trgm",
                table: "mail_messages",
                column: "SearchFrom")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_mail_messages_search_recipients_trgm",
                table: "mail_messages",
                column: "SearchRecipients")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_mail_messages_search_subject_trgm",
                table: "mail_messages",
                column: "SearchSubject")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ux_mail_messages_archive_source",
                table: "mail_messages",
                columns: new[] { "ArchiveId", "SourceMessageKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mail_attachments");

            migrationBuilder.DropTable(
                name: "mail_deletion_tombstones");

            migrationBuilder.DropTable(
                name: "mail_import_items");

            migrationBuilder.DropTable(
                name: "mail_import_runs");

            migrationBuilder.DropTable(
                name: "mail_messages");

            migrationBuilder.DropTable(
                name: "mail_folders");

            migrationBuilder.DropTable(
                name: "mail_archives");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");
        }
    }
}
