namespace NubArca.Api.Domain.Ai;

// Owner/file-scoped extracted-text container (OCR or native text), keyed by
// (file, profile). Owner-scoped to keep potentially sensitive extracted text
// inside the owner's boundary; never exposed through public APIs.
//
// `Text` is internal-only (like BlobMetadata.RawMetadataJson) — it is never
// placed in a normal/public DTO.
//
// A ROW HERE IS NOT AUTHORITY. It is a cache of an extraction that happened at
// some point in the past, and the file it describes may since have been deleted
// or moved into the Private Vault. Retrieval re-establishes eligibility from
// the live FileItem on every question — see OwnerDocumentEligibility. Cleaning
// these rows up is housekeeping, not the privacy boundary.
public class DocumentText
{
    public Guid Id { get; set; }

    public Guid FileItemId { get; set; }

    // WHICH BYTES were extracted. Blobs are content-addressed and immutable, so
    // this is an exact idempotence key: unchanged means the same object id, and
    // a content change is always a different one.
    //
    // It is what makes a rename or a move cost nothing. Those are DB-only
    // operations that leave the blob alone, so the extraction, the chunks and
    // every embedding are still correct — and re-deriving them would be an hour
    // of inference bought by renaming a folder.
    //
    // INTERNAL ONLY, like every other blob identifier: it never appears in a
    // DTO, a log line, a citation or a prompt.
    public Guid SourceBlobObjectId { get; set; }

    // How the text was READ into chunks. Its own version, deliberately not
    // RagIndexFormat: bumping the system one re-chunks the repository corpus,
    // and people's own documents should not pay for a change that did not
    // affect how they are read. See OwnerDocumentChunkFormat.
    //
    // Defaults to 0, which is not any released format, so the first pass after
    // an upgrade re-chunks rows written before the column existed.
    public int ChunkFormatVersion { get; set; }

    // Explicit owner scope (the file's owner), denormalized for owner-scoped
    // queries and isolation checks.
    public Guid OwnerUserId { get; set; }

    public Guid ProfileId { get; set; }

    // How the text was obtained ("native" | "pdf" | "ocr").
    public string Source { get; set; } = string.Empty;

    // Extraction status (see AiArtifactStatuses). This file-scoped row is an
    // explicit owned record, so it may legitimately carry a non-terminal value.
    public string Status { get; set; } = string.Empty;

    public string? ErrorCode { get; set; }

    // Hash of the extracted text (dedup / change detection). Not the blob SHA.
    public string? TextHash { get; set; }

    // Full extracted text. INTERNAL ONLY — never serialized to a normal DTO.
    public string? Text { get; set; }

    public int? CharCount { get; set; }

    public string? Language { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
