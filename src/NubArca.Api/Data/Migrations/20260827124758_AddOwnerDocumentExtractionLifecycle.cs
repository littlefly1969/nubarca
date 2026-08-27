using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <summary>
    /// The three columns the owner-private extraction lifecycle needs.
    ///
    /// Purely ADDITIVE, and there is nothing to backfill: `document_texts` and
    /// `document_chunks` were defined by an earlier slice and nothing has ever
    /// written to them. That is worth stating rather than assuming, because the
    /// defaults below would be wrong if it were not true — and each of them is
    /// chosen to fail in the safe direction if it ever stops being.
    ///
    /// `SourceBlobObjectId` defaults to the empty GUID, which never matches a
    /// real blob id, so any pre-existing row would be re-extracted rather than
    /// treated as current. `ChunkFormatVersion` defaults to 0, which is not any
    /// released chunk format, so any pre-existing row would be re-chunked. Both
    /// defaults mean "we do not know", and the indexer reads "we do not know" as
    /// "derive it again" — never as "it is fine".
    ///
    /// `SourceBlobObjectId` is deliberately NOT a foreign key. It records WHICH
    /// BYTES were read, for idempotence; it is not a reference that should keep a
    /// blob alive or cascade when one is purged. Reference counting belongs to
    /// FileItem, and a cache row must not participate in storage lifetime.
    /// </summary>
    public partial class AddOwnerDocumentExtractionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChunkFormatVersion",
                table: "document_texts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceBlobObjectId",
                table: "document_texts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "Heading",
                table: "document_chunks",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_texts_source_blob",
                table: "document_texts",
                column: "SourceBlobObjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_document_texts_source_blob",
                table: "document_texts");

            migrationBuilder.DropColumn(
                name: "ChunkFormatVersion",
                table: "document_texts");

            migrationBuilder.DropColumn(
                name: "SourceBlobObjectId",
                table: "document_texts");

            migrationBuilder.DropColumn(
                name: "Heading",
                table: "document_chunks");
        }
    }
}
