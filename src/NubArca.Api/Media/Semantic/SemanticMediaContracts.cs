using NubArca.Api.Files;

namespace NubArca.Api.Media.Semantic;

// VSEM-03: contracts of the unified photo+video semantic search
// (GET /api/media/semantic). Additive next to the photo-only
// /api/images/semantic, which is unchanged.
//
// Safety mirrors every other media DTO: results are FileItem-level and
// owner-scoped; no similarity score, no vector, no BlobObjectId, ProfileId,
// model name, storage key or sample/segment id is ever exposed. The only
// semantic addition is bounded TEMPORAL EVIDENCE for videos.

// One temporal match inside one video result. All values are whole
// milliseconds inside the video's own timeline. For photos every field is
// null and EvidenceType stays "visual".
public sealed record SemanticBestMatch(
    // Currently always "visual" (SigLIP2 frame evidence). A closed vocabulary,
    // so later evidence kinds (transcript, face) can be added additively.
    string EvidenceType,
    long? StartMilliseconds,
    long? EndMilliseconds,
    long? RepresentativeMilliseconds)
{
    public const string Visual = "visual";

    public static readonly SemanticBestMatch Photo = new(Visual, null, null, null);

    public static SemanticBestMatch ForSegment(long start, long end, long representative)
        => new(Visual, start, end, representative);
}

// One ranked result: the normal owner-visible media DTO plus its semantic
// evidence. `BestMatch` is always present (photos carry the null-temporal
// variant); `AdditionalMatches` holds up to MaxAdditionalMatches further
// distinct, non-overlapping segment intervals for videos, best-first.
public sealed record SemanticMediaResultItem(
    MediaItem Media,
    SemanticBestMatch BestMatch,
    IReadOnlyList<SemanticBestMatch> AdditionalMatches);

// SEARCH-SEM-01: one ranked hit inside the cached ranking snapshot. INTERNAL to
// the search pipeline — it is never serialized, which is what lets it carry the
// raw cosine score that the public DTO deliberately withholds. Promoted out of
// MediaSemanticSearchService only so the ranking cache and the bounded
// accumulator can be typed over it.
public sealed record SemanticRankedHit(
    Guid FileItemId,
    double Score,
    SemanticBestMatch BestMatch,
    IReadOnlyList<SemanticBestMatch> AdditionalMatches);

// HTTP response envelope for GET /api/media/semantic. `SemanticStatus` is the
// same closed vocabulary the photo semantic path surfaces: "ok" | "indexing"
// (many eligible candidates are not embedded yet) — "unavailable" is a 503
// with a sanitized reason, never a 200.
public sealed record SemanticMediaSearchResponse(
    IReadOnlyList<SemanticMediaResultItem> Items,
    string? NextCursor,
    bool HasMore,
    string SemanticStatus,
    int Total);

// Internal service result. Mirrors SemanticPhotosPage/GallerySemanticPage:
// Available=false carries only a sanitized reason token.
public sealed record SemanticMediaPage(
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<SemanticMediaResultItem> Items,
    string? NextCursor,
    bool HasMore,
    int Total,
    bool StillIndexingManyItems)
{
    public static SemanticMediaPage Unavailable(string? reason) => new(
        false, reason, Array.Empty<SemanticMediaResultItem>(), null, false, 0, false);
}
