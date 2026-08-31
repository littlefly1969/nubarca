using NubArca.Api.Domain;

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
    bool RequireUploadApproval,
    // Slideshow timing + per-participant quotas. Owner-facing, so the settings
    // panel can render the CURRENT values rather than guessing the defaults.
    // Quotas use the domain's 0 = unlimited.
    int PhotoSlideSeconds = PartySlideshowDefaults.PhotoSeconds,
    int MaxVideoSlideSeconds = PartySlideshowDefaults.MaxVideoSeconds,
    int MaxPhotoUploadsPerParticipant = 0,
    int MaxVideoUploadsPerParticipant = 0,
    // When true, new guest MESSAGES wait for approval before reaching the TV.
    // Independent of RequireUploadApproval, and owner-only to change.
    bool RequireMessageApproval = false,
    bool GameEnabled = false,
    int MinChallengeIntervalSeconds = PartyChallengeDefaults.MinIntervalSeconds,
    int MaxChallengeIntervalSeconds = PartyChallengeDefaults.MaxIntervalSeconds,
    int VotesPerGuest = PartyChallengeDefaults.VotesPerGuest,
    int? MaxChallengesPerSession = null);

// Derived public URLs for an active party link (relative, e.g. "/party/{token}"
// and "/party/{token}/upload"). Never a token hash. UploadUrl is null when the
// upload sub-switch is off.
// `Slideshow` is null only when there is no active link. Carried here rather
// than as loose scalars so the TV context stays one nested object and the
// timing travels with the link it belongs to — resolved in the SAME query that
// derives the URLs, so no N+1 appears.
public sealed record PartyLinkUrls(
    string ViewUrl,
    string? UploadUrl,
    PartySlideshowTimingDto? Slideshow = null);

// TV-facing slideshow timing for an active party link. Seconds, not
// milliseconds, matching the owner-facing contract.
public sealed record PartySlideshowTimingDto(int PhotoSeconds, int MaxVideoSeconds);

// Safe result of an anonymous party upload batch — counts + safe codes only.
// Never storage keys, blob ids, SHA, paths, stack traces, or created file ids.
//
// `Accepted` remains the TOTAL accepted count so an existing client keeps
// working; the per-kind breakdown and the quota fields are additive. Remaining
// values are null when that kind is unlimited, so a client can render
// "illimitate" without having to know that 0 means something special.
public sealed record PartyUploadResultDto(
    int Accepted,
    int Rejected,
    int AcceptedPhotos = 0,
    int AcceptedVideos = 0,
    int QuotaRejectedPhotos = 0,
    int QuotaRejectedVideos = 0,
    int? RemainingPhotos = null,
    int? RemainingVideos = null);

// What a guest may still upload on this link, for the upload page's header.
// Deliberately carries NO participant id and no token — the identity lives in
// an HttpOnly cookie the page never reads.
public sealed record PartyUploadSessionDto(
    int? MaxPhotos,
    int? MaxVideos,
    int UsedPhotos,
    int UsedVideos,
    int? RemainingPhotos,
    int? RemainingVideos);

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
    bool RequireUploadApproval = false,
    // Per-participant quotas carried straight off the resolved link (0 =
    // unlimited), so the upload path needs no second query to learn them.
    int MaxPhotoUploadsPerParticipant = 0,
    int MaxVideoUploadsPerParticipant = 0,
    // The link's message-approval mode, carried for the same reason: the
    // message submission path must not re-query the link it was just resolved
    // from to learn whether the greeting starts pending or live.
    bool RequireMessageApproval = false);

// --- PUBLIC (anonymous) party DTOs ---
// Deliberately minimal. NO owner identity, GPS, DateTaken, raw metadata,
// filenames, face/person data, AI data, storage/blob ids, SHA, paths, vectors,
// similarity scores, or token/hash. Item ids are logical FileItem ids used only
// to address token-scoped derived media; media is always metadata-stripped and
// downscaled (never originals).
public sealed record PartyAlbumDto(
    string AlbumName,
    int ItemCount,
    string? CoverUrl = null,
    string? ContributionUrl = null,
    bool GameEnabled = false);

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

// --- PARTY GUEST MESSAGES ---

// What the guest gets back after writing a greeting. Deliberately three fields:
// the id so the page can key its own optimistic list, the status so it can say
// "live" or "waiting to be approved", and the timestamp. NEVER the owner, the
// moderator, the party link, the participant, or the token.
public sealed record PartyMessageSubmissionDto(
    Guid Id,
    string Status, // "visible" | "pending"
    DateTime CreatedAt);

// Why a submission was refused. The HTTP layer maps these to a status code and
// a message; the service never formats copy of its own.
public enum PartyMessageSubmissionError
{
    // Empty, whitespace-only, or longer than the body limit after normalisation.
    InvalidBody,

    // Present but longer than the name limit after normalisation.
    InvalidDisplayName,
}

public sealed record PartyMessageSubmissionResult(
    PartyMessageSubmissionDto? Message,
    PartyMessageSubmissionError? Error)
{
    public static PartyMessageSubmissionResult Ok(PartyMessageSubmissionDto message) =>
        new(message, null);

    public static PartyMessageSubmissionResult Fail(PartyMessageSubmissionError error) =>
        new(null, error);
}

// Owner/delegate view of ONE message. Carries the text and the moderation
// state, and no identity beyond the name the guest chose to type: never the
// owner id, the moderator id, the participant id, or the party link id.
public sealed record PartyMessageDto(
    Guid Id,
    string? DisplayName,
    string Text,
    string Status,
    DateTime CreatedAt,
    DateTime? ModeratedAt,
    bool IsHero,
    DateTime? HeroPromotedAt);

// The manager queue for an album's CURRENT party. `CanManage` is always true
// here (a caller who cannot manage never receives this object) and exists so
// the client does not have to infer its own authority; `IsOwner` is what the
// UI uses to decide whether to render the owner-only approval switch, since a
// delegate moderates messages but never changes party settings.
public sealed record PartyMessageListDto(
    Guid AlbumId,
    bool PartyActive,
    bool RequireMessageApproval,
    bool IsOwner,
    IReadOnlyList<PartyMessageDto> Items);

// TV projection of the live message feed. One flat list, oldest first, already
// filtered to the currently active party and to Visible; the TV decides which
// of them to ribbon and which are Hero from `IsHero` alone.
public sealed record TvPartyMessageDto(
    Guid Id,
    string? DisplayName,
    string Text,
    DateTime CreatedAt,
    bool IsHero,
    DateTime? HeroPromotedAt);

public sealed record TvPartyMessagesDto(IReadOnlyList<TvPartyMessageDto> Messages);

// --- PARTY CHALLENGES ---
public sealed record PartyChallengeDto(
    Guid Id, string Title, string Body, string Kind, Guid? MediaFileItemId,
    string? MediaUrl, bool IsEnabled, int SortOrder, int VoteCount,
    DateTime CreatedAt, DateTime UpdatedAt);

public sealed record PartyChallengeListDto(Guid AlbumId, IReadOnlyList<PartyChallengeDto> Items);

public sealed record PartyChallengeWriteRequest(
    string? Title, string? Body, string? Kind, Guid? MediaFileItemId,
    bool IsEnabled = true);

public sealed record PartyChallengeReorderRequest(IReadOnlyList<Guid>? ChallengeIds);

public sealed record PartyGameSettingsRequest(
    bool GameEnabled, int MinChallengeIntervalSeconds, int MaxChallengeIntervalSeconds,
    int VotesPerGuest, int? MaxChallengesPerSession);

public sealed record PartyGuestChallengeDto(
    Guid Id, string Title, string Body, string Kind, string? MediaUrl, bool Voted);

public sealed record PartyGuestChallengesDto(
    string AlbumName, int VotesPerGuest, int VotesUsed, int VotesRemaining,
    IReadOnlyList<PartyGuestChallengeDto> Items);

public sealed record PartyVoteResultDto(bool Voted, int VotesUsed, int VotesRemaining);

public sealed record PartyChallengePresentationDto(
    Guid Id, string Title, string Body, string Kind, string? MediaUrl);

public sealed record PartyPlaybackSnapshotDto(
    string Mode, PartyChallengePresentationDto? ActiveChallenge,
    DateTime? NextChallengeAt, int CompletedCount);
