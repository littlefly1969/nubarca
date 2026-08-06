namespace NubArca.Api.Domain;

// A single anonymous "find your face" search run by a party guest against ONE
// party album. Short-lived (TTL, see Party:FaceSearch:SessionTtlMinutes) and
// deliberately minimal: it records only that a search happened, its safe status,
// how many album items matched, and (in PartyFaceSearchResult) the logical file
// ids of the matches in internal rank order.
//
// PRIVACY (see CLAUDE.md AI product + party rules):
//   * The full uploaded selfie is NEVER stored (processed in memory, then
//     discarded). The ONLY persisted derivative is a small crop of the detected
//     query face (FaceCropBlobObjectId) used as the TV face-filter indicator
//     thumbnail — an explicitly designed exception, served ONLY through the
//     TV-session-scoped face-thumbnail endpoint and deleted with the search.
//   * The query face embedding is NEVER persisted (search is synchronous; only
//     the resulting FileItem ids are kept).
//   * No similarity score, face id, person id, person name, cluster id, or raw
//     vector is ever stored here or surfaced.
//   * Album-scoped + owner-scoped: results only ever reference visible members of
//     THIS owner's THIS party album; visibility is re-derived on every read, so a
//     later hide/remove/pending drops an item without touching this row.
//
// TV ACTIVATION: a search is LOCAL to the guest's phone until it is explicitly
// activated for the TV. TvActivationVersion is a server-assigned monotonic
// per-album counter (server-side ordering — never a client timestamp): the
// highest-version activated, unexpired, ready session is THE active TV filter.
public class PartyFaceSearchSession
{
    public Guid Id { get; set; }

    // The album owner. Every read/write of a search is scoped to this owner.
    public Guid OwnerUserId { get; set; }

    // The party album the guest searched within.
    public Guid AlbumId { get; set; }

    // The party link the search came through, when known. Nullable so the row
    // survives a link rotation/revocation and needs no backfill.
    public Guid? PartyAlbumLinkId { get; set; }

    // Search state (see PartyFaceSearchStatuses). Only "ready" carries results.
    public string Status { get; set; } = PartyFaceSearchStatuses.Ready;

    // Number of album items that matched at creation time. The live result count
    // can be lower on a later read if items were hidden/removed since.
    public int ResultCount { get; set; }

    public DateTime CreatedAt { get; set; }

    // When this search stops being usable (both as a public result page and as
    // the TV active filtered slideshow). Activation extends it by one TTL so an
    // activated filter does not vanish moments later.
    public DateTime ExpiresAt { get; set; }

    // Server-assigned monotonic activation order within the album. Null = never
    // activated for the TV (or deactivated again). The highest version among
    // activated, unexpired, ready sessions is the album's active TV filter, so a
    // newer accepted activation replaces the previous one and a stale request
    // can never overwrite it.
    public long? TvActivationVersion { get; set; }

    // Server time of the last accepted activation (informational; ordering is
    // TvActivationVersion, never a timestamp).
    public DateTime? TvActivatedAt { get; set; }

    // Small derived crop of the DETECTED query face (never the full selfie),
    // stored in the derived blob store. Used only as the TV face-filter
    // indicator thumbnail; the reference is released when the search is deleted.
    public Guid? FaceCropBlobObjectId { get; set; }
}

// One matched album item for a face search, in internal rank order (most similar
// first). Carries ONLY the logical FileItem id + an opaque rank — never a score,
// vector, face id, or person id. Visibility is re-checked against the live album
// on every read, so a rank row for a now-hidden item is simply skipped.
public class PartyFaceSearchResult
{
    public Guid Id { get; set; }

    public Guid PartyFaceSearchSessionId { get; set; }

    // The matched logical file (a visible member of the album at search time).
    public Guid FileItemId { get; set; }

    // Internal ordering only (0 = best match). NOT a similarity score; the raw
    // cosine that produced it is never stored or exposed.
    public int Rank { get; set; }

    public DateTime CreatedAt { get; set; }
}

// Safe, greppable status tokens for a party face search (mirrors the codebase
// style of PartyUploadStatuses / MediaCategories). These are also the machine
// codes the public API returns so the frontend can show localized copy.
public static class PartyFaceSearchStatuses
{
    // The search ran and (possibly zero) matches were found.
    public const string Ready = "ready";

    // No face was detected in the uploaded selfie.
    public const string NoFace = "no_face";

    // The uploaded bytes were not a decodable/allowed image.
    public const string InvalidImage = "invalid_image";

    // Face search is not available (AI disabled, no face model/weights, feature
    // switched off, or the face embedder could not process the selfie). This is an
    // environment/config state, never a content failure.
    public const string Unavailable = "unavailable";
}
