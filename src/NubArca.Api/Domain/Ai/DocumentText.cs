namespace NubArca.Api.Domain.Ai;

// Owner/file-scoped extracted-text container (OCR or native text), keyed by
// (file, profile). Owner-scoped to keep potentially sensitive extracted text
// inside the owner's boundary; never exposed through public APIs.
//
// `Text` is internal-only (like BlobMetadata.RawMetadataJson) — it is never
// placed in a normal/public DTO. Phase 0A defines the container only; nothing
// populates it yet.
public class DocumentText
{
    public Guid Id { get; set; }

    public Guid FileItemId { get; set; }

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
