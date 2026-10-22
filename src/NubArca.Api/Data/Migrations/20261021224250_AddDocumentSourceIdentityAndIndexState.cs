using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentSourceIdentityAndIndexState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_document_texts_current_per_file",
                table: "document_texts");

            migrationBuilder.DropIndex(
                name: "ux_document_texts_file_profile",
                table: "document_texts");

            migrationBuilder.AlterColumn<Guid>(
                name: "FileItemId",
                table: "document_texts",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceId",
                table: "document_texts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "SourceKind",
                table: "document_texts",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "file");

            migrationBuilder.AddColumn<string>(
                name: "SourceRevision",
                table: "document_texts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            // EVERY EXISTING ROW IS A FILE READING. SourceKind already defaults
            // to 'file'; its SourceId is the FileItem it was always keyed by. Done
            // before the unique indexes and the check below, which would
            // otherwise see every existing row as the same zero source.
            migrationBuilder.Sql(
                "UPDATE document_texts SET \"SourceId\" = \"FileItemId\" WHERE \"FileItemId\" IS NOT NULL;");

            // A FILE READING'S SOURCE IS ITS FILE, stated in the database. The
            // trigger keeps the previous application compatible: it inserts rows
            // that name only the FileItem, and they arrive with the right source
            // identity instead of colliding on the zero id. The check constraint
            // added below makes the same rule impossible to break from either
            // application.
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION document_texts_file_source_identity() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.""FileItemId"" IS NOT NULL AND NEW.""SourceKind"" = 'file' THEN
        NEW.""SourceId"" := NEW.""FileItemId"";
    END IF;
    RETURN NEW;
END
$$;
CREATE TRIGGER trg_document_texts_file_source_identity
    BEFORE INSERT OR UPDATE ON document_texts
    FOR EACH ROW EXECUTE FUNCTION document_texts_file_source_identity();");

            migrationBuilder.CreateTable(
                name: "document_source_states",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Phase = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SourceRevision = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    FormatVersion = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_source_states", x => x.Id);
                    table.CheckConstraint("ck_document_source_states_attempts_non_negative", "\"Attempts\" >= 0");
                    table.ForeignKey(
                        name: "FK_document_source_states_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_texts_file",
                table: "document_texts",
                column: "FileItemId");

            migrationBuilder.CreateIndex(
                name: "ux_document_texts_current_per_source",
                table: "document_texts",
                columns: new[] { "SourceKind", "SourceId" },
                unique: true,
                filter: "\"IsCurrent\"");

            migrationBuilder.CreateIndex(
                name: "ux_document_texts_source_profile",
                table: "document_texts",
                columns: new[] { "SourceKind", "SourceId", "ProfileId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_texts_file_source",
                table: "document_texts",
                sql: "\"SourceKind\" <> 'file' OR (\"FileItemId\" IS NOT NULL AND \"FileItemId\" = \"SourceId\")");

            migrationBuilder.CreateIndex(
                name: "ix_document_source_states_owner_phase_status",
                table: "document_source_states",
                columns: new[] { "OwnerUserId", "Phase", "Status" });

            migrationBuilder.CreateIndex(
                name: "ux_document_source_states_source_phase",
                table: "document_source_states",
                columns: new[] { "SourceKind", "SourceId", "Phase" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_source_states");

            migrationBuilder.DropIndex(
                name: "ix_document_texts_file",
                table: "document_texts");

            migrationBuilder.DropIndex(
                name: "ux_document_texts_current_per_source",
                table: "document_texts");

            migrationBuilder.DropIndex(
                name: "ux_document_texts_source_profile",
                table: "document_texts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_texts_file_source",
                table: "document_texts");

            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS trg_document_texts_file_source_identity ON document_texts;
DROP FUNCTION IF EXISTS document_texts_file_source_identity();");

            // Only file readings can exist under the previous schema. Any other
            // kind is derived data the job rebuilds; it is dropped so the
            // FileItem column can become required again.
            migrationBuilder.Sql("DELETE FROM document_texts WHERE \"FileItemId\" IS NULL;");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "document_texts");

            migrationBuilder.DropColumn(
                name: "SourceKind",
                table: "document_texts");

            migrationBuilder.DropColumn(
                name: "SourceRevision",
                table: "document_texts");

            migrationBuilder.AlterColumn<Guid>(
                name: "FileItemId",
                table: "document_texts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_document_texts_current_per_file",
                table: "document_texts",
                column: "FileItemId",
                unique: true,
                filter: "\"IsCurrent\"");

            migrationBuilder.CreateIndex(
                name: "ux_document_texts_file_profile",
                table: "document_texts",
                columns: new[] { "FileItemId", "ProfileId" },
                unique: true);
        }
    }
}
