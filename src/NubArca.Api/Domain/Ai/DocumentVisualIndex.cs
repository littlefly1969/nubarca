namespace NubArca.Api.Domain.Ai;

/// A COMPLETE visual reading of one document, or nothing.
///
/// This row is the publication record for a set of rendered, embedded pages —
/// and its only interesting property is that it is written LAST. Pages are
/// rendered one at a time, embedded, and their rows staged; the index reaches
/// `Completed` when every required unit succeeded, in the same transaction that
/// stages the last one. Retrieval joins `Status == Completed` and nothing else,
/// so a document whose page 13 failed contributes zero hits from pages 1–12
/// rather than a silently partial corpus.
///
/// That is the same rule Slice 4 applies to text, restated for a derivative
/// that is even easier to publish partially: rendering is per page, so "index
/// what worked" is one `continue` away, and an owner searching a twenty-page
/// contract would get results from the eighteen pages that rendered with
/// nothing anywhere saying the other two are missing.
///
/// LIKE EVERY OTHER DERIVED ROW, THIS IS NOT AUTHORITY. It records that a
/// rendering happened; whether the file may still be read is recomputed from
/// the live `FileItem` on every query — see OwnerDocumentVisualEligibility.
public class DocumentVisualIndex
{
    public Guid Id { get; set; }

    public Guid FileItemId { get; set; }

    /// Explicit owner scope, denormalized for owner-scoped queries. A copy, and
    /// never the authority: retrieval reads the owner off the live FileItem.
    public Guid OwnerUserId { get; set; }

    /// WHICH BYTES were rendered. Blobs are content-addressed and immutable, so
    /// this is an exact idempotence key AND the instant-invalidation mechanism:
    /// retrieval requires this to equal the file's CURRENT blob, so replacing a
    /// document's content makes every visual row about it unreachable on the
    /// next question, with no sweeper involved.
    ///
    /// Internal only. Never a DTO field, never a log line, never a citation.
    public Guid SourceBlobObjectId { get; set; }

    /// HOW the pages were drawn — engine, pagination and bundled fonts, as one
    /// stable token (see DocumentVisualRenderProfiles). Separate from the
    /// embedding profile because the two change for different reasons: a new
    /// LibreOffice repaginates a DOCX without changing what SigLIP2 means, and a
    /// new visual checkpoint reinterprets pixels that are still the same pixels.
    public string RenderProfileKey { get; set; } = string.Empty;

    /// The visual embedding profile the units were embedded under. Two profiles
    /// are two coordinate systems; a cosine between them is a number with no
    /// meaning, so every read matches this exactly and never "the newest".
    public Guid EmbeddingProfileId { get; set; }

    /// `completed` | `failed` | `skipped` (see AiArtifactStatuses). There is no
    /// `partial`: a rendering that did not finish leaves whatever status it had,
    /// and only `completed` is queryable.
    public string Status { get; set; } = string.Empty;

    public string? ErrorCode { get; set; }

    /// How many visual units this index published. Zero is never `Completed` —
    /// an index with no pages is not a complete reading of a document, it is a
    /// renderer that produced nothing.
    public int UnitCount { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
