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

// Approval body. For an owner who does not yet have a Personal Area PIN the
// approval is ATOMIC with first PIN creation: PersonalPin/-Confirmation are
// REQUIRED and both the pairing approval and the PIN row commit together (or
// not at all). An owner who already has a PIN omits them (values are ignored —
// an existing PIN is never replaced from the pairing flow).
public sealed record TvPairingApprovalRequest(
    string PairingSecret,
    string? PersonalPin = null,
    string? PersonalPinConfirmation = null);

public enum TvPairingApproveStatus
{
    NotFound,
    // Owner has no Personal Area PIN and none (or an invalid one) was supplied —
    // pairing stays pending; nothing is committed.
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
    string? PartyUploadUrl = null);

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

public sealed record TvPersonalUnlockRequest(string Pin);

public sealed record TvPersonalUnlockDto(string UnlockToken, DateTime ExpiresAt);

public sealed record TvPersonalStatusDto(bool PinConfigured, bool Unlocked);

public sealed record TvPersonalHomeDto(string DisplayName, bool GalleryAvailable);

// Owner-side (normal auth) PIN management. UpdatedAt is the last time the PIN
// was set or changed (null when unconfigured) — never the hash, salt,
// generation, attempt counters, or grant details.
public sealed record TvPersonalPinStatusDto(bool Configured, DateTime? UpdatedAt = null);

public sealed record TvPersonalPinSetRequest(string Pin, string ConfirmPin);

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
