namespace NubArca.Api.Tv;

public sealed record TvPairingStartedDto(
    string PublicCode,
    string PairingSecret,
    string ApprovalUrl,
    DateTime ExpiresAt);

public sealed record TvPairingStatusDto(string Status, DateTime ExpiresAt);

// `Language` is the paired owner's persisted UI language ("it" | "en") so the
// TV app can localize its 10-foot UI in the owner's language. It is a bare
// language code — NOT owner identity (no name/email/id) — and defaults to
// Italian if the owner row is somehow missing.
public sealed record TvSessionDto(string Status, DateTime ExpiresAt, DateTime LastSeenAt, string Language);

// Approval body. For an owner who does not yet have a Personal Area credential
// the approval is ATOMIC with creating the DIRECTIONAL code:
// PersonalCode/-Confirmation are REQUIRED and both the pairing approval and the
// credential row commit together (or not at all). An owner who already has one
// omits them (values are ignored — an existing credential is never replaced
// from the pairing flow).
public sealed record TvPairingApprovalRequest(
    string PairingSecret,
    string? PersonalCode = null,
    string? PersonalCodeConfirmation = null);

public enum TvPairingApproveStatus
{
    NotFound,
    // Owner has no Personal Area credential and none (or an invalid one) was
    // supplied — pairing stays pending; nothing is committed.
    PinRequired,
    InvalidPin,
    PinMismatch,
    Approved,
}

// PairingId/PinCreated are for endpoint-side auditing only — never serialized.
public sealed record TvPairingApproveResult(
    TvPairingApproveStatus Status,
    TvPairingStatusDto? Response = null,
    Guid? PairingId = null,
    bool PinCreated = false);

public sealed record TvPairingPollResult(
    TvPairingStatusDto Response,
    string? NewSessionToken = null,
    DateTime? SessionExpiresAt = null);

// --- TV media browsing (ShowOnTv allowlist) ---
// All URLs are relative and rooted at /api/tv so the path-scoped TV session
// cookie is sent. DTOs carry only display-safe fields: no owner identity,
// StorageKey, BlobObjectId, SHA, paths, raw metadata, GPS/DateTaken, AI/face
// data, vectors, or similarity scores.

// PartyUrl is a RELATIVE public party landing URL ("/party/{token}") included
// ONLY when the album is ShowOnTv AND party mode is enabled — so the paired TV
// can render a QR. It is not a token hash; the TV never receives the hash. When
// party mode is off the URL is null and PartyEnabled is false.
public sealed record TvAlbumDto(
    Guid Id,
    string Name,
    int ItemCount,
    string? CoverThumbnailUrl,
    bool PartyEnabled = false,
    string? PartyUrl = null,
    // Relative public UPLOAD landing URL ("/party/{uploadToken}/upload"), present
    // only when party mode AND the upload sub-switch are on. Separate token from
    // PartyUrl; never a token hash.
    string? PartyUploadUrl = null);

public sealed record TvAlbumItemDto(
    Guid Id,
    string Name,
    string MediaType,
    // Display (rotation-aware for video) pixel dimensions, so the TV grid can
    // lay out proportional tiles from the DTO alone — never by loading a
    // thumbnail/poster. Null when the blob has not been probed. No new byte
    // exposure: dimensions are already-public display geometry.
    int? Width,
    int? Height,
    string ThumbnailUrl,
    string PreviewUrl,
    string? PosterUrl,
    string? VideoUrl,
    string? PreviewStripUrl = null);

public sealed record TvAlbumItemsDto(
    Guid Id,
    string Name,
    IReadOnlyList<TvAlbumItemDto> Items,
    bool PartyEnabled = false,
    string? PartyUrl = null,
    string? PartyUploadUrl = null,
    // Slideshow timing for THIS album's active party link, or null when party
    // mode is off. Nested rather than four loose scalars so the TV reads one
    // typed object whose absence is the "not a party" case, and so adding a
    // future timing value does not widen this record again. The TV never calls
    // an owner API — this is how the configured timing reaches it.
    TvPartySlideshowDto? PartySlideshow = null);

// Party slideshow timing as the TV consumes it: seconds, matching the owner
// contract, converted to milliseconds by the client at the point of use.
public sealed record TvPartySlideshowDto(int PhotoSeconds, int MaxVideoSeconds);

// --- TV active face-search (party face filter) ---
// A guest's face search reaches the TV only after an EXPLICIT "show on TV"
// activation on the party page. `Active` is false (with empty Items) when
// nothing is activated. `ActivationVersion` is the server-assigned monotonic
// activation order (the TV uses it to distinguish a replacing activation);
// `ActivatedAt` is the server activation/update time. `FaceThumbnailUrl` is a
// relative /api/tv URL for the small detected-face indicator crop (null when
// no crop is stored) — never the guest's full selfie, never an original. Items
// reuse the same /api/tv media URLs and carry NO names/scores/face/person data.
public sealed record TvFaceSearchActiveDto(
    bool Active,
    Guid? SearchId,
    long? ActivationVersion,
    DateTime? ActivatedAt,
    string? FaceThumbnailUrl,
    IReadOnlyList<TvAlbumItemDto> Items);

// --- TV Personal Area ---
// The unlock DTO carries the RAW opaque grant exactly once (the server stores
// only its hash). Status/home DTOs are minimal by design: capability flags and
// the owner's display name only — no email, id, roles, or profile fields, and
// never a PIN, hash, token hash, or storage/AI internals.

// `Code` is the current directional secret; `Pin` is accepted only so an
// already-installed television running the previous native contract keeps
// unlocking until its APK is replaced. Which of the two is even eligible is
// decided server-side by the stored scheme.
public sealed record TvPersonalUnlockRequest(string? Code = null, string? Pin = null)
{
    public string? Secret => Code ?? Pin;
}

public sealed record TvPersonalUnlockDto(string UnlockToken, DateTime ExpiresAt);

// `Scheme` tells the TV which credential the owner currently holds
// ("dpad-v1" | "pin-v1"), so a television running the directional-only UI can
// say "configure the new TV code from your account" instead of presenting an
// entry surface that can never succeed. Null only when nothing is configured.
public sealed record TvPersonalStatusDto(bool PinConfigured, bool Unlocked, string? Scheme = null);

public sealed record TvPersonalHomeDto(string DisplayName, bool GalleryAvailable);

// Owner-side (normal auth) credential management. UpdatedAt is the last time
// the secret was set or changed (null when unconfigured) — never the hash,
// salt, generation, attempt counters, or grant details.
public sealed record TvPersonalPinStatusDto(
    bool Configured, DateTime? UpdatedAt = null, string? Scheme = null);

// Owner-side directional-code set/change/reset. The plaintext reaches only the
// hash function; it is never stored, logged, audited or echoed back.
public sealed record TvPersonalDpadCodeSetRequest(string Code, string ConfirmCode);

// --- TV Personal Gallery (grant-gated projection of the owner image gallery) ---
// Same query semantics as the authenticated web gallery (/api/images) via the
// shared GalleryQueryParser + FileItemService.ListImagesPageAsync; the DTOs are
// a TV-safe projection: derived-media URLs rooted at /api/tv/personal (session
// cookie + unlock grant re-checked on every byte request), and only display
// fields — no storage/blob/AI internals, no owner identity, no original URLs.

public sealed record TvPersonalGalleryItemDto(
    Guid Id,
    string Name,
    // Always "image" today — the web gallery this projects is images-only
    // (videos are a separate web surface). Kept explicit so a future video
    // slice can extend without a breaking change.
    string MediaType,
    int? Width,
    int? Height,
    DateTime CreatedAt,
    string ThumbnailUrl,
    string PreviewUrl,
    bool Favorite,
    // Duplicate-collapsing annotation (parity with the web gallery): 1 unless
    // collapseDuplicates was requested and the blob has multiple references.
    int OccurrenceCount = 1);

public sealed record TvPersonalGalleryPageDto(
    IReadOnlyList<TvPersonalGalleryItemDto> Items,
    string? NextCursor,
    bool HasMore,
    // Server-authoritative count of items matching the CURRENT filter set (the
    // same shared query, minus paging). Stable across the pages of one query so
    // the TV viewer can show "position / total" without loading every page;
    // reflects duplicate-collapsed rows when collapsing is active. On a semantic
    // query this is the REDUCED semantic result total (≤ Top-K), not the physical
    // candidate count — the correct slideshow denominator.
    int TotalCount,
    // Slice 100 semantic metadata. SemanticActive=false → a normal filtered page.
    // SemanticStatus: "ok" | "unavailable" (engine/profile not ready) |
    // "indexing" (many filtered items not yet embedded). Never leaks model/index
    // internals; the client shows a generic notice.
    bool SemanticActive = false,
    int SemanticTopK = 0,
    string? SemanticStatus = null);

// Owner-private TV video gallery. Paths stay under /api/tv/personal and every
// JSON/derived/stream request revalidates both the TV session and unlock grant.
public sealed record TvPersonalVideoItemDto(
    Guid Id,
    string Name,
    int? Width,
    int? Height,
    DateTime CreatedAt,
    double? DurationSeconds,
    string? VideoCodec,
    string? AudioCodec,
    bool HasAudio,
    string PosterUrl,
    string VideoUrl,
    string PreviewStripUrl,
    int OccurrenceCount = 1);

public sealed record TvPersonalVideoPageDto(
    IReadOnlyList<TvPersonalVideoItemDto> Items,
    string? NextCursor,
    bool HasMore,
    int TotalCount);

// People-filter options: the SAME owner person identities the web people
// filter uses (safe ids + display name + face count) — never face ids, boxes,
// embeddings, cluster or representative-face data.
public sealed record TvPersonalPersonDto(Guid Id, string? Name, int FaceCount);

// Album-picker options for the selection "add to album" action. Names/counts
// only; ShowOnTv and party state are irrelevant here and deliberately omitted.
public sealed record TvPersonalAlbumDto(Guid Id, string Name, int ItemCount);

// --- Unified TV Personal media workspace ---
// One DTO for photos AND videos, matching the web MediaItem discriminator so
// the "Tutti" tab renders a mixed, server-ordered grid without the client
// merging two lists. Kind-specific fields are null on the other kind. Every URL
// is a grant-gated /api/tv/personal path; nothing storage-, blob- or AI-related
// is present, and GPS is not exposed here at all.
public sealed record TvPersonalMediaItemDto(
    Guid Id,
    // "image" | "video" — the discriminator the TV switches on.
    string Kind,
    // Title when the owner set one, else the file name (the shared
    // MediaDisplayName rule, so the TV and the web label an item identically).
    string DisplayName,
    int? Width,
    int? Height,
    DateTime CreatedAt,
    DateTime? TakenAt,
    bool Favorite,
    int? Rating,
    int OccurrenceCount,
    // Grid card image: small thumbnail (photo) or poster (video).
    string CardImageUrl,
    // Viewer image: medium preview (photo) or poster (video).
    string ViewerImageUrl,
    // ---- video-only (null on photos) ----
    string? VideoUrl,
    string? PreviewStripUrl,
    double? DurationSeconds,
    string? VideoCodec,
    bool? HasAudio);

public sealed record TvPersonalMediaPageDto(
    IReadOnlyList<TvPersonalMediaItemDto> Items,
    string? NextCursor,
    bool HasMore,
    // Server-authoritative total for the CURRENT query (paging-independent),
    // plus the per-kind split so the All/Photos/Videos tabs can show counts
    // without extra round-trips. On a single-kind query the other count is 0.
    int TotalCount,
    int PhotoCount,
    int VideoCount);

// Owner album card for the Personal Area album shelf. Counts and up to four
// cover image URLs, all re-pointed at the grant-gated TV byte routes. No
// ShowOnTv, no party token, no description, no storage internals.
public sealed record TvPersonalAlbumCardDto(
    Guid Id,
    string Name,
    int ItemCount,
    int PhotoCount,
    int VideoCount,
    IReadOnlyList<string> CoverImageUrls);

public sealed record TvPersonalFavoriteRequest(bool Favorite);

public sealed record TvPersonalFavoriteDto(Guid Id, bool Favorite);

public sealed record TvPersonalAlbumAddRequest(IReadOnlyList<Guid>? FileItemIds);

// Shared request/result for grant-gated Personal Gallery bulk destinations.
// Only owner-visible FileItem ids and client-safe reasons are returned: never
// blob ids, hashes, paths, storage keys, or destination internals.
public sealed record TvPersonalGalleryBulkRequest(IReadOnlyList<Guid>? FileItemIds);

public sealed record TvPersonalGalleryBulkFailureDto(Guid ItemId, string Reason);

public sealed record TvPersonalGalleryBulkResultDto(
    int Requested,
    int Succeeded,
    int Skipped,
    IReadOnlyList<Guid> SucceededItemIds,
    IReadOnlyList<TvPersonalGalleryBulkFailureDto> Failures);

// Curated viewer metadata: the TV-safe subset of the owner metadata endpoint
// (owner-private flow — GPS presence only, never coordinates; no serials, no
// raw embedded document, no storage internals).
public sealed record TvPersonalMediaInfoDto(
    Guid Id,
    string Name,
    long SizeBytes,
    int? Width,
    int? Height,
    DateTime DateTaken,
    string DateTakenSource,
    string? CameraMake,
    string? CameraModel,
    string? LensModel,
    int? Iso,
    double? Aperture,
    string? ExposureTime,
    double? FocalLength,
    bool HasGps,
    string? Title,
    string? Description,
    IReadOnlyList<string> Tags,
    int? Rating,
    bool Favorite,
    string? Location);

// --- Owner-side TV device/session management ---
// Safe projection of a TvSession for the OWNER to review/revoke. Never carries
// the session token, its hash, the pairing secret, or the owner id.
public sealed record TvDeviceDto(
    Guid Id,
    string? DeviceLabel,
    string? UserAgent,
    string Status,
    DateTime CreatedAt,
    DateTime LastSeenAt,
    DateTime ExpiresAt,
    DateTime? RevokedAt);
