using NubArca.Api.Domain;

namespace NubArca.Api.Party;

// Owner-facing party status for an album. Carries the derived public party URL
// (relative, e.g. "/party/{token}") ONLY to the owner-authorized caller so they
// can display/copy/QR it — never the token hash or any storage internal.
public sealed record AlbumPartyStatusDto(
    Guid AlbumId,
    // The Party this album is the `main` media source of, once one exists. It
    // is how the compatibility surface hands the owner the identity of the
    // product root without a second request — and it is the OWNER's own id on
    // an owner-authenticated route, never a public one.
    Guid? PartyId,
    // Reported, never required. Party and Show-on-TV are two independent
    // publication decisions: a party can run with no television in the room.
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
    int MaxMessagesPerParticipant = 0,
    // When true, new guest MESSAGES wait for approval before reaching the TV.
    // Independent of RequireUploadApproval, and owner-only to change.
    bool RequireMessageApproval = false,
    bool GameEnabled = false,
    int MinChallengeIntervalSeconds = PartyChallengeDefaults.MinIntervalSeconds,
    int MaxChallengeIntervalSeconds = PartyChallengeDefaults.MaxIntervalSeconds,
    int VotesPerGuest = PartyChallengeDefaults.VotesPerGuest,
    int? MaxChallengesPerSession = null,
    // Whether the guests are asked which activities they would like to see,
    // before the match starts. Off by default, which is what every party before
    // the column meant.
    bool PriorityVotingEnabled = false,
    // THE THREE CONTRIBUTIONS, as the host configures them. Photographs are
    // `UploadEnabled` above — the switch that already existed, and no second
    // one was invented for them. These are the other two, and each is a
    // separate product decision: a shared album with nothing on the television,
    // a book with no album, or all three at once.
    bool SlideshowMessagesEnabled = true,
    bool GuestbookEnabled = false,
    bool RequireGuestbookApproval = false);

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
    int? RemainingVideos,
    // Greetings, reported the same way as media: null max and null remaining
    // mean the host set no limit.
    int? MaxMessages = null,
    int UsedMessages = 0,
    int? RemainingMessages = null,
    // WHETHER THIS PARTY TAKES GREETINGS AT ALL. The contribution page holds
    // two halves on one token, and this is how it knows whether the written
    // half exists — so a party that takes photographs and no greetings shows a
    // page about photographs rather than a disabled tab. True by default, which
    // is what every party before the switch meant.
    bool SlideshowMessagesEnabled = true);

// Result of enabling party mode: the same status plus a convenience flag that
// the frontend can use to surface the (re)generated link.
public sealed record PartyEnableResult(
    Guid AlbumId,
    Guid PartyId,
    Guid LinkId,
    string PartyUrl);

// Resolved public access grant from a validated token — the WHOLE of what one
// public Party request is allowed to touch. Never leaves the service layer.
//
// This is the seam. The resolver walks
//     token -> PartyAlbumLink -> Party -> PartyMediaSource(main) -> Album
// once, and everything downstream keeps working on (OwnerUserId, MainAlbumId)
// exactly as it did before Party existed. Adapt once at the entrance, reuse
// everything after it: no service acquires a second, PartyId-shaped copy of
// itself.
//
// `Capabilities` is the host's role, resolved in the same pass, so an endpoint
// asks what the party may offer without re-deriving it. For an UPLOAD grant the
// record also carries the link's approval mode and per-guest quotas so the
// upload path needs no second query (view grants leave these at their
// defaults).
public sealed record PartyAccess(
    Guid PartyId,
    Guid OwnerUserId,
    // The party's `main` media source. Named for what it IS rather than
    // "AlbumId": a party may have more than one album, and every service
    // downstream is being handed this one deliberately.
    Guid MainAlbumId,
    // Always present: a grant is only ever produced by resolving a link.
    Guid PartyAlbumLinkId,
    // What the host's ROLE permits, with the PHASE already folded in: a live
    // capability outside the party is not a capability. Every endpoint keeps
    // its single check and none of them grows an `if (status …)` of its own.
    PartyCapabilities Capabilities,
    // Which of the three surfaces this guest is standing in front of, and how
    // much of it is open. Resolved once, at the seam, by the central policy.
    PartyGuestExperience Experience,
    bool RequireUploadApproval = false,
    // Per-participant quotas carried straight off the resolved link (0 =
    // unlimited), so the upload path needs no second query to learn them.
    int MaxPhotoUploadsPerParticipant = 0,
    int MaxVideoUploadsPerParticipant = 0,
    // The link's message-approval mode, carried for the same reason: the
    // message submission path must not re-query the link it was just resolved
    // from to learn whether the greeting starts pending or live.
    bool RequireMessageApproval = false,
    // Greetings one guest may send, on the same principle and with the same
    // 0 = unlimited convention as the media quotas above.
    int MaxMessagesPerParticipant = 0,
    // WHETHER THIS PARTY TAKES GREETINGS FOR THE SLIDESHOW AT ALL. A different
    // question from whether they need approving: approval is about what a
    // greeting becomes, this is about whether there is anywhere to write one.
    // True by default, which is what every party before the column meant.
    bool SlideshowMessagesEnabled = true,
    // Whether this party keeps a guest book. Opt-in, so an absent value is the
    // same "no book here" every party had before it existed.
    bool GuestbookEnabled = false,
    bool RequireGuestbookApproval = false);

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
    bool GameEnabled = false,
    // The print capability's own URL, non-null ONLY while printing is genuinely
    // open: configured, enabled, on a live station whose printer does 10x15, and
    // with budget left in at least one product. Null is how the guest hub knows
    // there is no print card to show — never a disabled tile.
    string? PrintUrl = null,
    // The live game's own URL, on the same principle: non-null exactly while
    // this party has a hosted game to walk into. The hub builds no route of its
    // own from GameEnabled — a capability says where it lives, or it is absent.
    string? GameUrl = null);

// THE guest context: one authoritative answer for the canonical QR route.
//
// It replaces the album-shaped landing DTO because the concept is no longer an
// album — it is what this guest is looking at right now, which changes as the
// evening does while the URL never does. One landing, one fetch: a client must
// not have to combine two reads to learn one state.
//
// Deliberately NOT twenty `showX` booleans. `Content` holds only the slots that
// are enabled AND belong to this phase AND are allowed by the access mode;
// `Capabilities` holds only what is genuinely available. Absence IS the answer,
// which is what keeps a disabled tile from ever being rendered.
//
// Carries no owner id, no album id, no party id, no link id, no token, no hash,
// no storage internal, no GPS/EXIF and no AI internal.
public sealed record PartyGuestContextDto(
    string Title,
    // "before" | "live" | "after" — which of the three surfaces to render.
    string Phase,
    // "full" | "library-only" — how much of that surface is open.
    string AccessMode,
    DateTime? EventStartsAt,
    string? AlbumName,
    int ItemCount,
    // The invitation's hero before the party: the invitation's own photograph,
    // else the album's CHOSEN cover, else null for a branded composition.
    string? CoverUrl,
    // The GUEST projection of each slot: a photograph is an address on this
    // token, never the owner's file id.
    IReadOnlyList<PartyGuestContentViewDto> Content,
    PartyGuestCapabilitiesDto Capabilities,
    PartyGuestLibraryDto Library);

// Where a capability LIVES, or nothing. The hub builds no route of its own from
// a boolean: a capability states where it is, or it is absent. Face search has
// no URL of its own because it happens on the landing itself, so it is the one
// flag here.
public sealed record PartyGuestCapabilitiesDto(
    string? ContributionUrl = null,
    string? GameUrl = null,
    string? PrintUrl = null,
    bool FaceSearch = false,
    // THE TWO WRITTEN CONTRIBUTIONS, each stating where it lives or not being
    // here at all. They are separate fields rather than one because they are
    // separate things: a greeting is written to be read out on a television and
    // a dedication is written to be kept, and a party may take either, both, or
    // neither.
    //
    // The slideshow composer shares the contribution URL — same token, same
    // session, same page, a different half of it — so it is that URL with the
    // composer asked for. It is absent whenever the party takes no greetings,
    // which is what stops the guest surface from drawing a card, a tab, a form,
    // an empty state or a fetch for a contribution that does not exist here.
    string? SlideshowMessageUrl = null,
    // The book's own route on the VIEW token. Present whenever the book is
    // READABLE — during the party and for as long as the memories last — and
    // absent otherwise, so a keepsake outlives the composer that filled it.
    string? GuestbookUrl = null);

// Whether the memories are reachable, and until when if the host said so.
public sealed record PartyGuestLibraryDto(bool Available, DateTime? AccessEndsAt = null);

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
    IReadOnlyList<PartyItemDto> Items,
    /// <summary>
    /// Where the face is in the selfie the guest just took, as fractions — so
    /// their phone can frame what it already holds. Null on every status but a
    /// completed search, and null when a stored search is re-read later: by then
    /// the selfie is long gone from the phone and there is nothing to frame.
    /// </summary>
    PartyFaceBoxDto? Face = null);

/// <summary>The detected face, in fractions of the analysed image.</summary>
public sealed record PartyFaceBoxDto(double X, double Y, double Width, double Height);

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
    DateTime CreatedAt,
    // What the guest has left, so a page can say so without a second request.
    // Null when the host set no limit — never 0, which a client would read as
    // "none left" rather than "no limit".
    int? MessagesRemaining = null);

// Why a submission was refused. The HTTP layer maps these to a status code and
// a message; the service never formats copy of its own.
public enum PartyMessageSubmissionError
{
    // Empty, whitespace-only, or longer than the body limit after normalisation.
    InvalidBody,

    // Present but longer than the name limit after normalisation.
    InvalidDisplayName,

    // This guest has spent the greetings the host allowed them. A PRODUCT
    // limit, deliberately distinct from rate limiting: one says the party has a
    // budget, the other says the requests are coming too fast. They get
    // different status codes because a client must be able to tell them apart.
    LimitReached,

    // THIS PARTY DOES NOT TAKE GREETINGS FOR THE SLIDESHOW. Not a quota, not a
    // rate limit and not a moderation decision: there is no composer on the
    // guest surface at all, and a request that reached here was built by hand.
    // Refused by the SERVER rather than by the absence of a form, because a
    // surface the browser stops drawing is not a rule.
    Disabled,
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
    IReadOnlyList<PartyMessageDto> Items,
    // Whether the party is still TAKING greetings for the slideshow. The queue
    // is reachable either way — closing a channel must never lock somebody out
    // of the queue it filled — so this is what lets the surface say "nothing
    // here is being shown" instead of leaving a manager to infer it from a
    // television that went quiet.
    bool SlideshowMessagesEnabled = true);

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
// The owner's view of one prepared activity. The three rule fields are optional
// on the wire with the domain defaults, so a client written before the composer
// still parses one and a client written after it still reads a legacy row.
public sealed record PartyChallengeDto(
    Guid Id, string Title, string Body, string Kind, Guid? MediaFileItemId,
    string? MediaUrl, bool IsEnabled, int SortOrder, int VoteCount,
    DateTime CreatedAt, DateTime UpdatedAt,
    int? DurationSeconds = null,
    string VotingMode = PartyChallengeVotingModes.Binary,
    string? VoteQuestion = null);

public sealed record PartyChallengeListDto(Guid AlbumId, IReadOnlyList<PartyChallengeDto> Items);

// A write from the composer. The rule fields default to the domain defaults, so
// a caller that only knows about title/body/kind/media — the settings panel
// before the composer existed — writes a valid activity without sending them.
public sealed record PartyChallengeWriteRequest(
    string? Title, string? Body, string? Kind, Guid? MediaFileItemId,
    bool IsEnabled = true,
    int? DurationSeconds = null,
    string? VotingMode = null,
    string? VoteQuestion = null);

public sealed record PartyChallengeReorderRequest(IReadOnlyList<Guid>? ChallengeIds);

public sealed record PartyGameSettingsRequest(
    bool GameEnabled, int MinChallengeIntervalSeconds, int MaxChallengeIntervalSeconds,
    int VotesPerGuest, int? MaxChallengesPerSession,
    // Nullable so a client written before pre-game preferences existed keeps
    // whatever the host already configured rather than silently switching them
    // off on its next save.
    bool? PriorityVotingEnabled = null);

public sealed record PartyGuestChallengeDto(
    Guid Id, string Title, string Body, string Kind, string? MediaUrl, bool Voted);

public sealed record PartyGuestChallengesDto(
    string AlbumName, int VotesPerGuest, int VotesUsed, int VotesRemaining,
    IReadOnlyList<PartyGuestChallengeDto> Items);

public sealed record PartyVoteResultDto(bool Voted, int VotesUsed, int VotesRemaining);

public sealed record PartyChallengePresentationDto(
    Guid Id, string Title, string Body, string Kind, string? MediaUrl,
    int? DurationSeconds = null,
    string VotingMode = PartyChallengeVotingModes.Binary,
    string? VoteQuestion = null);

public sealed record PartyPlaybackSnapshotDto(
    string Mode, PartyChallengePresentationDto? ActiveChallenge,
    DateTime? NextChallengeAt, int CompletedCount);

// --- PARTY GUEST BOOK ---
//
// A separate resource from the greetings above, with its own table, its own
// switch, its own moderation queue and no path whatsoever to a television. The
// DTOs are separate for the same reason the entity is: a shape shared with
// PartyMessage is one refactor away from a projection that reads both.

// ONE DEDICATION, as a guest reads it. The signature the author typed and the
// text, and nothing else — never the participant, the link, the moderator, the
// owner, or the status (a guest only ever receives entries that are public, so
// a status field could only ever say "visible").
public sealed record PartyGuestbookEntryDto(
    Guid Id,
    string? AuthorDisplayName,
    string Body,
    DateTime CreatedAt);

// THE BOOK, as a guest reads it. Newest first: the last thing written is the
// thing somebody just wrote, and a keepsake that opens on page one of a hundred
// is a keepsake nobody scrolls.
public sealed record PartyGuestbookPageDto(
    IReadOnlyList<PartyGuestbookEntryDto> Entries,
    // Whether the guest may add to it right now. Distinct from the book
    // existing: an ended party's book may still be readable while closed to new
    // dedications, and the surface says which.
    bool CanWrite,
    int MaxAuthorDisplayNameLength = PartyGuestbookLimits.MaxAuthorDisplayNameLength,
    int MaxBodyLength = PartyGuestbookLimits.MaxBodyLength);

// What the guest gets back after writing. The id so the page can key its own
// optimistic entry, and the status so it can say "in the book" or "waiting to
// be approved".
public sealed record PartyGuestbookSubmissionDto(
    Guid Id,
    string Status, // "visible" | "pending"
    DateTime CreatedAt);

// Why a dedication was refused. The HTTP layer maps these to a status code and
// a stable machine code; the service never formats copy of its own.
public enum PartyGuestbookSubmissionError
{
    // Empty, whitespace-only, or over the limit after normalisation.
    InvalidBody,

    // Present but over the limit after normalisation.
    InvalidAuthorDisplayName,

    // This party keeps no guest book. Same shape of refusal as a greeting sent
    // to a party that takes none: a hand-built request, refused by the server.
    Disabled,
}

public sealed record PartyGuestbookSubmissionResult(
    PartyGuestbookSubmissionDto? Entry,
    PartyGuestbookSubmissionError? Error)
{
    public static PartyGuestbookSubmissionResult Ok(PartyGuestbookSubmissionDto entry) =>
        new(entry, null);

    public static PartyGuestbookSubmissionResult Fail(PartyGuestbookSubmissionError error) =>
        new(null, error);
}

// Owner/delegate view of ONE dedication: the text, the signature, and the
// moderation state. No owner id, no moderator id, no participant id and no
// link id — the same restraint PartyMessageDto exercises, for the same reason.
public sealed record PartyGuestbookManagedEntryDto(
    Guid Id,
    string? AuthorDisplayName,
    string Body,
    string Status,
    DateTime CreatedAt,
    DateTime? ModeratedAt);

// The manager queue for a party's book. `IsOwner` is what the UI uses to decide
// whether to render the configuration switches, exactly as on the greetings
// queue: a delegate moderates the book and does not decide whether there is one.
public sealed record PartyGuestbookManagerListDto(
    Guid PartyId,
    bool GuestbookEnabled,
    bool RequireGuestbookApproval,
    bool IsOwner,
    IReadOnlyList<PartyGuestbookManagedEntryDto> Entries);

// --- CONTRIBUTION CONFIGURATION ---

// The host's (or a `contributions.configure` collaborator's) decision about
// which of the three contributions this party takes. Every field is nullable
// so a client that knows about one switch cannot switch the others off by
// saving the form it does know about — the same rule PartyGameSettingsRequest
// applies to PriorityVotingEnabled.
public sealed record PartyContributionSettingsRequest(
    bool? UploadEnabled = null,
    bool? RequireUploadApproval = null,
    bool? SlideshowMessagesEnabled = null,
    bool? RequireMessageApproval = null,
    bool? GuestbookEnabled = null,
    bool? RequireGuestbookApproval = null);
