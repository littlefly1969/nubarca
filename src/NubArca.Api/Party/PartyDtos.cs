namespace NubArca.Api.Party;

// Owner-facing party status for an album. Carries the derived public party URL
// (relative, e.g. "/party/{token}") ONLY to the owner-authorized caller so they
// can display/copy/QR it — never the token hash or any storage internal.
public sealed record AlbumPartyStatusDto(
    Guid AlbumId,
    bool ShowOnTv,
    bool PartyMode,
    string? PartyUrl,
    bool UploadEnabled,
    string? UploadUrl,
    // When true, new anonymous uploads wait for owner approval before appearing
    // on the public party page / TV. Default false (immediate visibility).
    bool RequireUploadApproval);

// Derived public URLs for an active party link (relative, e.g. "/party/{token}"
// and "/party/{token}/upload"). Never a token hash. UploadUrl is null when the
// upload sub-switch is off.
public sealed record PartyLinkUrls(string ViewUrl, string? UploadUrl);

// Safe result of an anonymous party upload batch — counts + safe codes only.
// Never storage keys, blob ids, SHA, paths, stack traces, or created file ids.
public sealed record PartyUploadResultDto(int Accepted, int Rejected);

// Result of enabling party mode: the same status plus a convenience flag that
// the frontend can use to surface the (re)generated link.
public sealed record PartyEnableResult(
    Guid AlbumId,
    Guid LinkId,
    string PartyUrl);

// Resolved public access grant from a validated token: which owner's which
// album the token unlocks. Never leaves the service layer. For an UPLOAD grant
// it also carries the resolving link id and its approval-mode flag so the upload
// service can record the correct initial moderation state (view grants leave
// these at their defaults).
public sealed record PartyAccess(
    Guid OwnerUserId,
    Guid AlbumId,
    Guid? PartyAlbumLinkId = null,
    bool RequireUploadApproval = false);

// --- PUBLIC (anonymous) party DTOs ---
// Deliberately minimal. NO owner identity, GPS, DateTaken, raw metadata,
// filenames, face/person data, AI data, storage/blob ids, SHA, paths, vectors,
// similarity scores, or token/hash. Item ids are logical FileItem ids used only
// to address token-scoped derived media; media is always metadata-stripped and
// downscaled (never originals).
public sealed record PartyAlbumDto(string AlbumName, int ItemCount);

public sealed record PartyItemDto(
    Guid Id,
    string MediaType, // "image" | "video"
    string ThumbnailUrl,
    string PreviewUrl,
    string? DownloadUrl); // null for videos (no playback/download in this slice)

public sealed record PartyItemsDto(string AlbumName, IReadOnlyList<PartyItemDto> Items);

// Safe result of a public party "find your face" search. `Status` is a machine
// code (PartyFaceSearchStatuses) the frontend maps to localized copy:
// "ready" | "no_face" | "invalid_image" | "unavailable". `SearchId` is present
// only for a ready search (so the guest/TV can re-fetch it). Items reuse the same
// token-scoped, metadata-stripped party media as the album grid. NO similarity
// score, face id, person id, person name, or vector is ever included.
public sealed record PartyFaceSearchResponseDto(
    string Status,
    Guid? SearchId,
    int ResultCount,
    IReadOnlyList<PartyItemDto> Items);

// Result of explicitly activating a search as the album's TV face filter. The
// version is the server-assigned monotonic activation order (an opaque counter
// — no identity, no timestamps from the client).
public sealed record PartyFaceSearchActivationDto(Guid SearchId, long ActivationVersion);

// --- OWNER-side party upload moderation DTO ---
// Safe, owner-private view of ONE guest upload. Carries only the logical file id
// (to address owner-auth thumbnail/removal), a display name, the media type, the
// moderation status, and safe timestamps. NEVER StorageKey, BlobObjectId, SHA,
// paths, token/hash, raw metadata, GPS, or face/person/AI data.
public sealed record PartyUploadItemDto(
    Guid FileItemId,
    string Name,
    string MediaType, // "image" | "video"
    string Status,    // "approved" | "pending" | "hidden" | "rejected" | "removed_from_album"
    string ThumbnailUrl,
    DateTime UploadedAt,
    DateTime? ModeratedAt);

public sealed record PartyUploadListDto(
    Guid AlbumId,
    bool RequireUploadApproval,
    IReadOnlyList<PartyUploadItemDto> Items);
