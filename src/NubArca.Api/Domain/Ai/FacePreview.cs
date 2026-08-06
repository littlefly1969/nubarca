namespace NubArca.Api.Domain.Ai;

// UI-ONLY derived artifact: a high-quality square crop of a single FaceDetection,
// generated from the ORIGINAL blob (same EXIF-orient convention as detection) and
// cached in the DERIVED store. Keyed by (FaceDetection, Size); blob-level like the
// detection, so it is shared across owners for the same blob — but every serve
// path re-checks owner + non-vault visibility.
//
// IMPORTANT: face previews are for display only. They are NEVER an embedding
// source — embeddings are always computed from the original blob + landmark
// alignment. Regenerable cache: safe to wipe.
public class FacePreview
{
    public Guid Id { get; set; }

    public Guid FaceDetectionId { get; set; }

    // Derived-store BlobObject holding the JPEG crop (content-addressed, refcount-
    // managed). Plain correlation id — never exposed.
    public Guid BlobObjectId { get; set; }

    // One of FacePreviewSizes.
    public string Size { get; set; } = string.Empty;

    public int Width { get; set; }
    public int Height { get; set; }

    public DateTime CreatedAt { get; set; }
}
